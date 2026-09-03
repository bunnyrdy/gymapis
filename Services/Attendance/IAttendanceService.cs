using GymApis.Dtos;
using GymApis.Services.Staff;

namespace GymApis.Services.Attendance;

public interface IAttendanceService
{
    /// <summary>The Shift Roaster for one day. Unmarked staff appear as not_marked, never missing.</summary>
    Task<PagedResult<AttendanceRosterItem>> RosterAsync(AttendanceQuery query, CancellationToken ct);

    /// <summary>The four cards above the roster, over the whole branch for that day.</summary>
    Task<AttendanceStatsResponse> StatsAsync(DateOnly? date, string? role, CancellationToken ct);

    /// <summary>Profile, this month's totals and the calendar grid for one person.</summary>
    Task<AttendanceDetailResponse?> DetailAsync(long staffId, DateOnly? month, CancellationToken ct);

    /// <summary>The paginated Attendance Log on the detail page.</summary>
    Task<ServiceResult<PagedResult<AttendanceLogItem>>> LogAsync(
        long staffId, AttendanceLogQuery query, CancellationToken ct);

    /// <summary>
    /// Records a mark. <paramref name="canBackdate"/> is the caller's
    /// ManageStaff privilege, passed in by the controller rather than read here
    /// so the authorization decision stays in one place.
    /// </summary>
    Task<ServiceResult<AttendanceRecordResponse>> MarkAsync(
        MarkAttendanceRequest req, bool canBackdate, CancellationToken ct);

    /// <summary>Rewrites an existing record. Always a privileged act.</summary>
    Task<ServiceResult<AttendanceRecordResponse>> CorrectAsync(
        long id, CorrectAttendanceRequest req, CancellationToken ct);

    /// <summary>
    /// Closes out every working day that has passed its shift end with nothing
    /// recorded. Idempotent; returns how many rows it wrote.
    /// </summary>
    Task<int> SweepAsync(CancellationToken ct);
}
