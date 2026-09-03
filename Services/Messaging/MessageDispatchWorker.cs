using GymApis.Models.Messaging;
using GymApis.Repos;
using GymApis.Services.Email;
using Microsoft.EntityFrameworkCore;

namespace GymApis.Services.Messaging;

/// <summary>
/// Drains message_queue.
///
/// Same shape as AttendanceCloseoutWorker — the framework's own
/// BackgroundService, a PeriodicTimer, a fresh DI scope per tick because the
/// worker is a singleton and everything it uses holds a DbContext. No Hangfire,
/// no Quartz, no tables of its own beyond the queue.
///
/// A tick sends at most <see cref="Batch"/> messages, once a minute. That is a
/// 600/hour ceiling against a 270/day budget, so the BUDGET is what actually
/// binds and the pacing is gentle: 350 renewal reminders go out as ~240 spread
/// across day one and the rest across day two, rather than 240 fired into
/// Brevo's rate limiter in a single minute from a brand-new sending domain.
///
/// Nothing here decides how much may be sent. claim_message_batch() does, in
/// SQL, atomically, so a second API instance running the identical tick claims
/// a disjoint set instead of double-spending the day.
/// </summary>
public class MessageDispatchWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    /// <summary>Messages per tick. See the class comment for why it is small.</summary>
    private const int Batch = 10;

    /// <summary>
    /// The retry ladder. Five attempts across a little over seven hours, which
    /// outlasts an ordinary provider incident without holding a reset link
    /// long past the hour its token lives.
    /// </summary>
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
    ];

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MessageDispatchWorker> _log;
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    public MessageDispatchWorker(IServiceScopeFactory scopes, ILogger<MessageDispatchWorker> log)
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
            return;     // shutting down before the first tick
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
            var settings = await sp.GetRequiredService<IMessagingSettings>().GetAsync(ct);
            var policy   = sp.GetRequiredService<IMessagingPolicy>();
            var sender   = sp.GetRequiredService<IEmailSender>();

            // The master switch. Off means member mail stops; account mail keeps
            // flowing unless the owner also ticked the second box, because
            // otherwise flipping one toggle locks every user — the owner
            // included — out of password recovery with no way back.
            if (!settings.EmailsEnabled && settings.SuspendTransactional)
                return;

            var highOnly = !settings.EmailsEnabled;

            // Ask for high priority only rather than claiming everything and
            // filtering: a normal row that is never claimed cannot be stranded
            // in 'sending' waiting for the ten-minute rescue.
            var claimed = await db.MessageQueue
                .FromSql($"""
                          SELECT * FROM claim_message_batch(
                              {MessageChannels.Email}, {settings.DailyEmailLimit},
                              {settings.HighPriorityReserve}, {Batch}, {highOnly})
                          """)
                .AsNoTracking()
                .ToListAsync(ct);

            foreach (var message in claimed)
            {
                ct.ThrowIfCancellationRequested();
                await DeliverAsync(db, policy, sender, message, ct);
            }

            await PurgeIfDueAsync(db, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not a failure. Anything left in 'sending' is rescued by
            // claim_message_batch() on the next process's first tick.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Message dispatch tick failed; will retry on the next interval.");
        }
    }

    private async Task DeliverAsync(
        GymDbContext db, IMessagingPolicy policy, IEmailSender sender,
        QueuedMessage message, CancellationToken ct)
    {
        // 1. Too late to be useful. This is what makes the kill switch safe to
        //    flip: the backlog it accumulates expires rather than arriving in a
        //    heap the moment somebody turns sending back on.
        if (message.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await SkipAsync(db, message.Id, MessageSkipReasons.Expired, ct);
            return;
        }

        // 2. The authoritative half of the two-call rule. The answer can have
        //    changed since this was queued — a member deactivated on Tuesday
        //    must not receive Monday's reminder on Wednesday.
        var verdict = await policy.MayReceiveAsync(
            message.Purpose, message.MemberId, message.UserId, ct);

        if (!verdict.Allowed)
        {
            await SkipAsync(db, message.Id, verdict.SkipReason!, ct);
            return;
        }

        EmailSendResult result;
        try
        {
            result = await sender.SendAsync(
                message.ToAddress, message.Subject ?? string.Empty,
                message.BodyHtml ?? string.Empty, message.BodyText, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A transport fault: DNS, socket, timeout. Indistinguishable from a
            // 5xx as far as the ladder is concerned.
            result = EmailSendResult.Fail(null, ex.Message);
        }

        if (result.Sent)
        {
            // One statement flips the row AND increments the day's counter, so
            // a crash cannot land between them and over-report what went out.
            await db.Database.ExecuteSqlAsync(
                $"SELECT mark_message_sent({message.Id}, {result.ProviderMessageId})", ct);
            return;
        }

        await RecordFailureAsync(db, message, result, ct);
    }

    /// <summary>
    /// Applies the ladder, or gives up. Note what does NOT happen here: the
    /// quota counter is untouched. Brevo does not charge for a request it
    /// rejected, and counting rejections would ration the day against sends
    /// that never happened.
    /// </summary>
    private async Task RecordFailureAsync(
        GymDbContext db, QueuedMessage message, EmailSendResult result, CancellationToken ct)
    {
        var attempts = message.Attempts + 1;
        var error = result.Error;

        if (attempts >= Backoff.Length)
        {
            _log.LogError(
                "Giving up on message {MessageId} ({Purpose}) after {Attempts} attempts: {Error}",
                message.Id, message.Purpose, attempts, error);

            await db.Database.ExecuteSqlAsync($"""
                UPDATE message_queue
                   SET status = 'failed', attempts = {attempts}, last_error = {error},
                       body_html = NULL, body_text = NULL
                 WHERE id = {message.Id}
                """, ct);
            return;
        }

        // 429 is not an ordinary failure: the allowance is gone, and it does not
        // come back until Brevo's counter rolls over at UTC midnight. Walking
        // the normal ladder would burn every remaining attempt inside seven
        // hours against a wall that has not moved.
        var retryAt = result.StatusCode == StatusCodes.Status429TooManyRequests
            ? NextUtcMidnight()
            : DateTimeOffset.UtcNow + Backoff[attempts - 1];

        await db.Database.ExecuteSqlAsync($"""
            UPDATE message_queue
               SET status = 'pending', claimed_at = NULL, attempts = {attempts},
                   last_error = {error}, next_attempt_at = {retryAt}
             WHERE id = {message.Id}
            """, ct);
    }

    /// <summary>
    /// Marks a message as deliberately not sent. The body is cleared with it:
    /// a skipped reset mail still holds a live link until its token lapses, and
    /// the row is about to sit in the table for thirty days.
    /// </summary>
    private static Task SkipAsync(GymDbContext db, long id, string reason, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            UPDATE message_queue
               SET status = 'skipped', skip_reason = {reason},
                   body_html = NULL, body_text = NULL
             WHERE id = {id}
            """, ct);

    /// <summary>
    /// DPDP retention, hourly. Cheap enough to run on a tick, rare enough not
    /// to be worth its own worker.
    /// </summary>
    private async Task PurgeIfDueAsync(GymDbContext db, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _lastPurge < TimeSpan.FromHours(1)) return;
        _lastPurge = DateTimeOffset.UtcNow;

        await db.Database.ExecuteSqlAsync($"SELECT purge_message_queue()", ct);
    }

    /// <summary>
    /// When the vendor's allowance resets. UTC, not the branch's midnight —
    /// Brevo counts in UTC and the two are five and a half hours apart.
    /// </summary>
    private static DateTimeOffset NextUtcMidnight() =>
        new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1), TimeSpan.Zero);

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
