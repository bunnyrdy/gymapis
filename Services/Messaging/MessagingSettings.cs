using GymApis.Models.Settings;
using GymApis.Repos;
using Microsoft.EntityFrameworkCore;

namespace GymApis.Services.Messaging;

public interface IMessagingSettings
{
    /// <summary>The tenant's row, created from configured defaults if missing.</summary>
    Task<AppSettings> GetAsync(CancellationToken ct);

    /// <summary>Drops the cache so the next read sees a write that just landed.</summary>
    void Invalidate();
}

/// <summary>
/// Reads `app_settings`, cached for a minute.
///
/// The cache exists for one caller: the dispatch worker reads these values on
/// every tick and once per message inside it, and none of that should be a
/// round trip. A minute is short enough that flipping the master switch takes
/// effect while the owner is still looking at the screen, and the write path
/// calls <see cref="Invalidate"/> anyway so the same process is immediate.
///
/// Static rather than per-instance because the service is scoped and a request
/// -lifetime cache would never be hit twice. The row belongs to the tenant, not
/// to the request — the same reasoning as BranchClock's cached timezone.
/// </summary>
public class MessagingSettings : IMessagingSettings
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static AppSettings? _cached;
    private static DateTimeOffset _cachedAt;

    private readonly GymDbContext _db;
    private readonly IConfiguration _config;

    public MessagingSettings(GymDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public async Task<AppSettings> GetAsync(CancellationToken ct)
    {
        if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < Ttl)
            return _cached;

        await Gate.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < Ttl)
                return _cached;

            // AsNoTracking: this instance is handed to the dispatcher, which
            // must never accidentally write through it. The settings controller
            // loads its own tracked copy.
            var row = await _db.AppSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.TenantId == Tenancy.TenantId, ct);

            // 009_messaging.sql seeds a row per tenant, so this is the path for
            // a tenant created afterwards rather than the normal case.
            if (row is null)
            {
                row = new AppSettings
                {
                    TenantId              = Tenancy.TenantId,
                    DailyEmailLimit       = _config.GetValue("Messaging:DailyLimit", 270),
                    HighPriorityReserve   = _config.GetValue("Messaging:HighPriorityReserve", 30),
                    ReminderDaysBefore    = _config.GetSection("Messaging:ReminderDaysBefore")
                                                   .Get<int[]>() ?? [3],
                };
                _db.AppSettings.Add(row);
                await _db.SaveChangesAsync(ct);
            }

            _cached = row;
            _cachedAt = DateTimeOffset.UtcNow;
            return row;
        }
        finally
        {
            Gate.Release();
        }
    }

    public void Invalidate() => _cached = null;
}
