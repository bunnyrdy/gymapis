using GymApis.Models.Messaging;
using GymApis.Repos;
using GymApis.Services.Attendance;   // IBranchClock
using Microsoft.EntityFrameworkCore;

namespace GymApis.Services.Messaging;

/// <summary>
/// Queues "your membership expires soon" reminders. It does not send anything —
/// MessageDispatchWorker does that, against the day's remaining budget.
///
/// Splitting generation from delivery is what lets 350 reminders exist as 350
/// rows on Monday and go out as 240 on Monday and 110 on Tuesday, without the
/// generator knowing anything about quotas.
///
/// HOURLY, NOT NIGHTLY, and that is the interesting decision. A nightly timer is
/// wrong the first time the process restarts at 02:05, or is down all night, or
/// runs on two instances — you get a duplicate reminder or none at all. Instead
/// the write is idempotent by construction, exactly like close_out_attendance():
/// every row carries dedupe_key = 'renewal:{membershipId}:{daysBefore}' under a
/// partial unique index, and the insert is ON CONFLICT DO NOTHING. Once that is
/// true, running it more often is free and a server that was down overnight
/// catches up on its first tick.
///
/// "Today" here is the BRANCH's day, from IBranchClock — the opposite of the
/// quota day in claim_message_batch(). The thing being measured is a membership
/// end date, which is a local calendar fact: between midnight and 05:30 IST a
/// UTC-derived date still says yesterday, which is a day of reminders sent early
/// or skipped entirely. Note this also rules out v_member_overview.days_remaining,
/// which the view computes from Postgres CURRENT_DATE.
/// </summary>
public class RenewalReminderWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RenewalReminderWorker> _log;

    public RenewalReminderWorker(IServiceScopeFactory scopes, ILogger<RenewalReminderWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);

        do
        {
            await RunOnceAsync(ct);
        }
        while (await SafeWaitAsync(timer, ct));
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var sp       = scope.ServiceProvider;
            var db       = sp.GetRequiredService<GymDbContext>();
            var clock    = sp.GetRequiredService<IBranchClock>();
            var queue    = sp.GetRequiredService<IMessageQueue>();
            var settings = await sp.GetRequiredService<IMessagingSettings>().GetAsync(ct);

            // Generation stops with the master switch too. Queuing reminders
            // nobody will receive would just fill the table with rows destined
            // to expire — and would deliver a surprise heap if the reminders
            // outlived their expiry window before somebody switched it back on.
            if (!settings.EmailsEnabled) return;

            var today = await clock.TodayAsync(ct);

            foreach (var daysBefore in settings.ReminderDaysBefore.Distinct())
            {
                if (daysBefore < 0) continue;
                await QueueForOffsetAsync(db, queue, today, daysBefore, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not a failure.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Renewal reminder tick failed; will retry on the next interval.");
        }
    }

    private async Task QueueForOffsetAsync(
        GymDbContext db, IMessageQueue queue, DateOnly today, int daysBefore, CancellationToken ct)
    {
        var target = today.AddDays(daysBefore);

        // Straight off `memberships`, not v_member_overview: the view derives
        // days_remaining from CURRENT_DATE, and the whole point of IBranchClock
        // is that CURRENT_DATE is the wrong clock for this.
        //
        // The join filters to active members with an address, which duplicates
        // what MessagingPolicy checks a moment later — deliberately, because
        // doing it in SQL turns a table scan of enqueue calls into a handful of
        // rows. The policy remains the authority; this is just the cheap pass.
        var due = await (
            from ms in db.Memberships
            join m in db.Members on ms.MemberId equals m.Id
            join p in db.MembershipPlans on ms.PlanId equals p.Id
            where ms.TenantId == Tenancy.TenantId
               && ms.BranchId == Tenancy.BranchId
               && ms.Status == "active"
               && ms.EndDate == target
               && m.Status == "active"
               && m.Email != null
            select new
            {
                MembershipId = ms.Id,
                MemberId     = m.Id,
                m.FullName,
                m.Email,
                PlanName     = p.Name,
                ms.EndDate,
            }).ToListAsync(ct);

        if (due.Count == 0) return;

        var keys = due.ToDictionary(r => r.MembershipId, r => $"renewal:{r.MembershipId}:{daysBefore}");

        // The dedupe index is what GUARANTEES exactly-once; this query is what
        // keeps the common case from relying on a caught exception. On the
        // second tick of the day every key is already here and nothing is
        // enqueued at all.
        var wanted = keys.Values.ToList();
        var already = await db.MessageQueue
            .Where(m => m.DedupeKey != null && wanted.Contains(m.DedupeKey))
            .Select(m => m.DedupeKey!)
            .ToHashSetAsync(ct);

        var queued = 0;
        foreach (var row in due)
        {
            var key = keys[row.MembershipId];
            if (already.Contains(key)) continue;

            var message = await queue.EnqueueAsync(
                MessagePurposes.RenewalReminder,
                row.Email!,
                MessageTemplates.RenewalReminder(row.FullName, row.PlanName, row.EndDate, daysBefore),
                memberId: row.MemberId,
                dedupeKey: key,
                ct: ct);

            if (message is not null) queued++;
        }

        if (queued == 0) return;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another instance queued the same cycle between the filter above
            // and this save. The index did its job: no duplicate exists. This
            // batch is lost, and the next tick — an hour away, and its filter
            // will now exclude whatever landed — queues whatever is still
            // missing. Self-healing is why the worker runs hourly rather than
            // once a night.
            _log.LogWarning(ex,
                "Renewal reminders for {Target} (+{Days}d) raced another instance; "
                + "the next tick will queue anything still missing.", target, daysBefore);
            return;
        }

        _log.LogInformation(
            "Renewal reminders for {Target} (+{Days}d): {Queued} queued of {Candidates} candidates.",
            target, daysBefore, queued, due.Count);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
