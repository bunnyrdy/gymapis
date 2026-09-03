using GymApis.Repos;
using Microsoft.EntityFrameworkCore;

namespace GymApis.Services.Attendance;

/// <summary>
/// What day is it *at the gym*?
///
/// Everywhere else in this codebase "today" is DateOnly.FromDateTime(UtcNow),
/// which is fine for a joining date nobody looks at twice. Attendance cannot
/// use it. The branch runs on Asia/Kolkata; between 18:30 and 00:00 local, UTC
/// still says yesterday, so a mark taken at 8pm would land on the wrong day and
/// the evening shift would be closed out five and a half hours early.
///
/// The timezone is per-branch data (`branches.timezone`), not a constant, so it
/// is read from the database — once, then cached for the process lifetime. A
/// gym does not relocate mid-request, and this is called on every roster read.
/// </summary>
public interface IBranchClock
{
    /// <summary>The current date in the branch's own calendar.</summary>
    Task<DateOnly> TodayAsync(CancellationToken ct);

    /// <summary>The current wall-clock time at the branch.</summary>
    Task<DateTime> NowAsync(CancellationToken ct);

    /// <summary>Renders an instant as the branch's local wall-clock time.</summary>
    Task<DateTime> ToLocalAsync(DateTimeOffset instant, CancellationToken ct);

    /// <summary>
    /// The instant local midnight falls on, for a date in the branch's calendar.
    ///
    /// The inverse of <see cref="ToLocalAsync"/>, and what a `timestamptz` range
    /// predicate needs: `paid_at &gt;= StartOfDay(1st) AND paid_at &lt; StartOfDay(1st
    /// of next month)` is "this month at the gym". Comparing a timestamptz
    /// against a UTC-derived boundary instead would attribute five and a half
    /// hours of every month to the wrong one.
    ///
    /// Returned as UTC — the same instant, written the only way Npgsql will
    /// send it. A DateTimeOffset carrying +05:30 is rejected outright as a
    /// `timestamp with time zone` parameter, and Postgres stores instants
    /// anyway: the offset was never going to survive the round trip.
    /// </summary>
    Task<DateTimeOffset> StartOfDayAsync(DateOnly localDate, CancellationToken ct);
}

public class BranchClock : IBranchClock
{
    private readonly GymDbContext _db;
    private readonly ILogger<BranchClock> _log;

    // Process-wide: the zone belongs to the branch, not to the request.
    private static TimeZoneInfo? _zone;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public BranchClock(GymDbContext db, ILogger<BranchClock> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<DateOnly> TodayAsync(CancellationToken ct) =>
        DateOnly.FromDateTime(await NowAsync(ct));

    public async Task<DateTime> NowAsync(CancellationToken ct) =>
        await ToLocalAsync(DateTimeOffset.UtcNow, ct);

    public async Task<DateTime> ToLocalAsync(DateTimeOffset instant, CancellationToken ct)
    {
        var zone = await ZoneAsync(ct);
        return TimeZoneInfo.ConvertTime(instant, zone).DateTime;
    }

    public async Task<DateTimeOffset> StartOfDayAsync(DateOnly localDate, CancellationToken ct)
    {
        var zone = await ZoneAsync(ct);
        var midnight = localDate.ToDateTime(TimeOnly.MinValue);

        // DateTimeKind.Unspecified is required: ConvertTimeToUtc rejects a Local
        // or Utc kind that disagrees with the zone it was handed. On the rare
        // date where local midnight does not exist (a DST jump — not in
        // Asia/Kolkata, but the zone is branch data and India is not the only
        // possible answer), GetUtcOffset still returns the offset in force, so
        // the boundary lands an hour out rather than throwing.
        midnight = DateTime.SpecifyKind(midnight, DateTimeKind.Unspecified);

        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight)).ToUniversalTime();
    }

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken ct)
    {
        if (_zone is not null) return _zone;

        await Gate.WaitAsync(ct);
        try
        {
            if (_zone is not null) return _zone;

            var name = await _db.Branches
                .Where(b => b.Id == Tenancy.BranchId && b.TenantId == Tenancy.TenantId)
                .Select(b => b.Timezone)
                .FirstOrDefaultAsync(ct);

            _zone = Resolve(name);
            return _zone;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Falls back to UTC rather than throwing. A missing or misspelled timezone
    /// should degrade the roster's date arithmetic, not take the whole API down
    /// — but it is logged as a warning, because silently running the gym on the
    /// wrong clock is exactly the bug this class exists to prevent.
    /// </summary>
    private TimeZoneInfo Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            _log.LogWarning(
                "Branch {BranchId} has no timezone set; attendance dates will use UTC.",
                Tenancy.BranchId);
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(name);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _log.LogWarning(ex,
                "Branch {BranchId} has an unusable timezone {TimeZone}; falling back to UTC.",
                Tenancy.BranchId, name);
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Test seam — drops the cached zone so the next call re-reads it.</summary>
    internal static void ResetCache() => _zone = null;
}
