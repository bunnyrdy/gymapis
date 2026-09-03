namespace GymApis.Models.Attendance;

/// <summary>
/// Keyless read model over the `v_staff_attendance_monthly` view.
///
/// The view already buckets attendance by month and computes days present,
/// days absent, working days and the percentage — which is exactly the four
/// cards on the attendance detail screen. Recomputing that in LINQ would
/// duplicate arithmetic the schema already owns, the same reason
/// <see cref="Members.MemberOverview"/> exists.
///
/// Note the view's JOIN is an INNER one: a staff member with no attendance rows
/// in a month produces no row here at all. The service treats a missing row as
/// zeros rather than as "not found".
///
/// Keyless means EF will not track or write it. Reads only.
/// </summary>
public class StaffAttendanceMonthly
{
    public long StaffId { get; set; }
    public long TenantId { get; set; }
    public long BranchId { get; set; }

    public string StaffCode { get; set; } = default!;
    public string FullName { get; set; } = default!;
    public string Role { get; set; } = default!;

    /// <summary>First day of the bucketed month — date_trunc('month', attendance_date).</summary>
    public DateOnly Month { get; set; }

    public long DaysPresent { get; set; }
    public long DaysAbsent { get; set; }
    public long DaysLeave { get; set; }
    public long WorkingDays { get; set; }

    /// <summary>Null when the month has no working days — the view guards the divide with NULLIF.</summary>
    public decimal? AttendancePct { get; set; }
}
