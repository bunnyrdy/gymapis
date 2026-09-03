using System.Text.Json;
using GymApis.Dtos;
using GymApis.Models.Common;
using GymApis.Models.Messaging;
using GymApis.Repos;
using GymApis.Services.Attendance;   // IBranchClock
using GymApis.Services.Staff;        // ServiceResult<T>
using Microsoft.EntityFrameworkCore;

namespace GymApis.Services.Messaging;

public interface ISettingsService
{
    Task<MessagingSettingsResponse> GetMessagingAsync(CancellationToken ct);
    Task<ServiceResult<MessagingSettingsResponse>> UpdateMessagingAsync(
        MessagingSettingsWriteRequest request, CancellationToken ct);
    Task<PagedResult<QueuedMessageResponse>> QueueAsync(
        string? status, string? purpose, int page, int pageSize, CancellationToken ct);
}

/// <summary>
/// The Messaging tab behind /api/settings. Reads and writes `app_settings`, and
/// reports on the queue without ever handing back a rendered body.
/// </summary>
public class SettingsService : ISettingsService
{
    /// <summary>Same ceiling every list in this API carries. Without one, ?pageSize=1000000 is a bulk export.</summary>
    private const int MaxPageSize = 100;

    private readonly GymDbContext _db;
    private readonly IMessagingSettings _settings;
    private readonly IBranchClock _clock;
    private readonly ICurrentUser _actor;

    public SettingsService(
        GymDbContext db, IMessagingSettings settings, IBranchClock clock, ICurrentUser actor)
    {
        _db = db;
        _settings = settings;
        _clock = clock;
        _actor = actor;
    }

    public async Task<MessagingSettingsResponse> GetMessagingAsync(CancellationToken ct)
    {
        var settings = await _settings.GetAsync(ct);
        return await ProjectAsync(settings, ct);
    }

    public async Task<ServiceResult<MessagingSettingsResponse>> UpdateMessagingAsync(
        MessagingSettingsWriteRequest request, CancellationToken ct)
    {
        var row = await _db.AppSettings
            .FirstOrDefaultAsync(s => s.TenantId == Tenancy.TenantId, ct);

        if (row is null)
        {
            row = new Models.Settings.AppSettings { TenantId = Tenancy.TenantId };
            _db.AppSettings.Add(row);
        }

        // The old values, for the audit row. "Why did nobody get their
        // reminders last month" needs an answer with a name and a timestamp on
        // it, and the only way to answer it is to record what changed.
        var before = new
        {
            row.EmailsEnabled,
            row.SuspendTransactional,
            row.DailyEmailLimit,
            row.HighPriorityReserve,
            row.ReminderDaysBefore,
        };

        row.EmailsEnabled        = request.EmailsEnabled;
        row.SuspendTransactional = request.SuspendTransactional;
        row.DailyEmailLimit      = request.DailyEmailLimit;
        row.HighPriorityReserve  = request.HighPriorityReserve;
        row.ReminderDaysBefore   = request.ReminderDaysBefore.Distinct().Order().ToArray();

        _db.ActivityLogs.Add(new ActivityLog
        {
            TenantId    = Tenancy.TenantId,
            BranchId    = Tenancy.BranchId,
            ActorUserId = _actor.UserId,
            Action      = "settings.messaging.updated",
            EntityType  = "app_settings",
            EntityId    = row.Id is 0 ? null : row.Id,
            Description = Describe(row),
            Metadata    = JsonSerializer.Serialize(new
            {
                before,
                after = new
                {
                    row.EmailsEnabled,
                    row.SuspendTransactional,
                    row.DailyEmailLimit,
                    row.HighPriorityReserve,
                    row.ReminderDaysBefore,
                },
            }),
        });

        await _db.SaveChangesAsync(ct);

        // The cache is process-wide and lives for a minute; without this the
        // owner flips the switch and watches nothing happen for 60 seconds.
        _settings.Invalidate();

        return ServiceResult<MessagingSettingsResponse>.Ok(await ProjectAsync(row, ct));
    }

    public async Task<PagedResult<QueuedMessageResponse>> QueueAsync(
        string? status, string? purpose, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _db.MessageQueue
            .Where(m => m.TenantId == Tenancy.TenantId);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(m => m.Status == status);

        if (!string.IsNullOrWhiteSpace(purpose))
            query = query.Where(m => m.Purpose == purpose);

        var total = await query.CountAsync(ct);

        // The projection is the allow-list: BodyHtml and BodyText are not in it,
        // so no future edit to this method can leak a live reset link.
        var items = await query
            .OrderByDescending(m => m.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new QueuedMessageResponse(
                m.Id, m.Purpose, m.Priority, m.Status, m.SkipReason,
                m.ToAddress, m.Subject, m.Attempts, m.LastError,
                m.CreatedAt, m.SentAt, m.ExpiresAt))
            .ToListAsync(ct);

        return new PagedResult<QueuedMessageResponse>(items, page, pageSize, total);
    }

    // -----------------------------------------------------------------------

    private async Task<MessagingSettingsResponse> ProjectAsync(
        Models.Settings.AppSettings settings, CancellationToken ct)
    {
        // The vendor's day, not the gym's — see MessageQuotaUsage.
        var quotaDate = DateOnly.FromDateTime(DateTime.UtcNow);

        var usage = await _db.MessageQuotaUsage.AsNoTracking()
            .FirstOrDefaultAsync(u => u.QuotaDate == quotaDate && u.Channel == MessageChannels.Email, ct);

        var highSent   = usage?.HighSent ?? 0;
        var normalSent = usage?.NormalSent ?? 0;

        // The same two ceilings claim_message_batch() applies, so the number on
        // the screen is the number the worker will actually honour rather than
        // a second, drifting calculation of it.
        var normalRemaining = Math.Max(
            Math.Min(settings.DailyEmailLimit - settings.HighPriorityReserve - normalSent,
                     settings.DailyEmailLimit - highSent - normalSent), 0);

        var pending = await _db.MessageQueue
            .CountAsync(m => m.TenantId == Tenancy.TenantId && m.Status == MessageStatuses.Pending, ct);
        var failed = await _db.MessageQueue
            .CountAsync(m => m.TenantId == Tenancy.TenantId && m.Status == MessageStatuses.Failed, ct);

        var resetsAtUtc = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1), TimeSpan.Zero);

        return new MessagingSettingsResponse(
            settings.EmailsEnabled,
            settings.SuspendTransactional,
            settings.DailyEmailLimit,
            settings.HighPriorityReserve,
            settings.ReminderDaysBefore,
            highSent,
            normalSent,
            normalRemaining,
            pending,
            failed,
            resetsAtUtc,
            await _clock.ToLocalAsync(resetsAtUtc, ct));
    }

    private static string Describe(Models.Settings.AppSettings row) => row switch
    {
        { EmailsEnabled: false, SuspendTransactional: true } =>
            "Turned off all outbound email, password resets included.",
        { EmailsEnabled: false } =>
            "Turned off member email; password resets still send.",
        _ => "Updated messaging settings.",
    };
}
