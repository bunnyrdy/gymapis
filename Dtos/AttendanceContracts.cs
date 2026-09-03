using System.ComponentModel.DataAnnotations;

namespace GymApis.Dtos;

/// <summary>
/// Allowed values for the attendance module.
///
/// <see cref="StoredStatuses"/> is deliberately narrower than the DB CHECK on
/// `staff_attendance.status`, which still permits late, half_day, leave,
/// holiday and week_off. V1 is Present/Absent per the spec; lateness is carried
/// by the `is_late` flag instead of a status of its own, so
/// v_staff_attendance_monthly keeps counting correctly without being rewritten.
/// Widening this array is the only change needed to expose the rest later.
///
/// <see cref="ClientStatuses"/> is what the screens display. Two of the four
/// are never stored: "not_marked" is the absence of a row before the shift has
/// ended, and "week_off" is a day outside the person's roster. Deriving them at
/// read time is what keeps the table meaning "something was expected here".
/// </summary>
public static class AttendanceEnums
{
    public static readonly string[] StoredStatuses = ["present", "absent"];

    public static readonly string[] ClientStatuses = ["present", "absent", "not_marked", "week_off"];

    /// <summary>Values of `staff.role` the roster can be narrowed to.</summary>
    public static readonly string[] Roles = ["trainer", "receptionist", "manager", "admin"];

    public const string Present   = "present";
    public const string Absent    = "absent";
    public const string NotMarked = "not_marked";
    public const string WeekOff   = "week_off";

    /// <summary>How a row came to exist. 'system' rows have no human actor.</summary>
    public const string ViaManual = "manual";
    public const string ViaSystem = "system";
}

// ---------------------------------------------------------------------------
// Write models
// ---------------------------------------------------------------------------

/// <summary>
/// Marking one person present or absent on one date.
///
/// SECURITY — what is deliberately NOT here: TenantId, BranchId, ShiftId,
/// MarkedByUserId, MarkedVia, IsLate and CheckInAt. Every one of those is
/// derived or taken from the validated token by the server. A caller who could
/// post `markedByUserId` could attribute their own edit to someone else, and a
/// caller who could post `isLate` could quietly launder a late arrival. The DTO
/// is the allow-list.
/// </summary>
public class MarkAttendanceRequest : IValidatableObject
{
    [Required]
    [Range(1, long.MaxValue, ErrorMessage = "Select a staff member.")]
    public long StaffId { get; set; }

    /// <summary>The day being marked, in the branch's own calendar.</summary>
    [Required]
    public DateOnly Date { get; set; }

    [Required]
    public string Status { get; set; } = default!;

    [StringLength(500)]
    public string? Notes { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (!AttendanceEnums.StoredStatuses.Contains(Status))
            yield return new ValidationResult(
                "Status must be either present or absent.", [nameof(Status)]);

        // A cheap sanity bound. The real "not in the future" test needs the
        // branch's clock and lives in the service; this only rejects input that
        // is nonsense under any timezone.
        if (Date > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1))
            yield return new ValidationResult(
                "Attendance cannot be marked for a future date.", [nameof(Date)]);

        if (Date < new DateOnly(2000, 1, 1))
            yield return new ValidationResult(
                "That date is too far in the past.", [nameof(Date)]);
    }
}

/// <summary>
/// Correcting a record that already exists. Separate from
/// <see cref="MarkAttendanceRequest"/> because it is a different privilege —
/// marking today is front-desk work, rewriting the log is not — and because the
/// staff member and date are not up for negotiation: they identify the row.
/// </summary>
public class CorrectAttendanceRequest : IValidatableObject
{
    [Required]
    public string Status { get; set; } = default!;

    [StringLength(500)]
    public string? Notes { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (!AttendanceEnums.StoredStatuses.Contains(Status))
            yield return new ValidationResult(
                "Status must be either present or absent.", [nameof(Status)]);
    }
}

// ---------------------------------------------------------------------------
// Read models
// ---------------------------------------------------------------------------

/// <summary>
/// One line of the Shift Roaster table.
///
/// <see cref="AttendanceId"/> is null when nothing has been recorded yet, which
/// is also what tells the client to offer both Mark buttons rather than a
/// correction.
/// </summary>
public record AttendanceRosterItem(
    long             StaffId,
    string           StaffCode,
    string           FullName,
    string?          PhotoUrl,
    string           Role,
    string?          JobTitle,
    string?          ShiftName,
    TimeOnly?        ShiftStart,
    TimeOnly?        ShiftEnd,
    // present | absent | not_marked | week_off
    string           Status,
    long?            AttendanceId,
    DateTimeOffset?  CheckInAt,
    DateTimeOffset?  CheckOutAt,
    bool             IsLate,
    // 'system' means nobody marked it — the sweep closed the day out.
    string?          MarkedVia,
    string?          Notes);

/// <summary>
/// The four cards above the roster. Counted over the whole branch for the day
/// on screen, not over the page — a card that changes when you paginate is
/// worse than no card. Derived from the same query that builds the rows, so the
/// two can never disagree.
/// </summary>
public record AttendanceStatsResponse(
    int TotalStaff,
    int Present,
    int Absent,
    int NotMarked);

/// <summary>One cell of the monthly calendar.</summary>
public record AttendanceDayItem(
    DateOnly        Date,
    string          Status,
    bool            IsLate,
    DateTimeOffset? CheckInAt);

/// <summary>
/// The detail page's four stat cards. Read from v_staff_attendance_monthly,
/// which already owns this arithmetic; a month with no rows comes back as
/// zeros rather than as a 404.
/// </summary>
public record AttendanceSummaryResponse(
    int      WorkingDays,
    int      DaysPresent,
    int      DaysAbsent,
    decimal? AttendancePct);

/// <summary>One row of the Attendance Log table on the detail page.</summary>
public record AttendanceLogItem(
    long            Id,
    DateOnly        Date,
    string?         ShiftName,
    DateTimeOffset? CheckInAt,
    DateTimeOffset? CheckOutAt,
    string          Status,
    bool            IsLate,
    string          MarkedVia,
    string?         Notes);

/// <summary>
/// Everything the attendance detail screen draws above the log: who the person
/// is, where they stand today, this month's totals, and the calendar grid.
/// </summary>
public record AttendanceDetailResponse(
    long      StaffId,
    string    StaffCode,
    string    FullName,
    string?   PhotoUrl,
    string    Role,
    string?   JobTitle,
    DateOnly  JoiningDate,
    string    StaffStatus,
    string?   ShiftName,
    TimeOnly? ShiftStart,
    TimeOnly? ShiftEnd,
    short[]   WorkingDays,

    // The month being displayed, as its first day.
    DateOnly  Month,
    // Today in the branch's calendar, so the client need not guess the timezone.
    DateOnly  Today,
    // present | absent | not_marked | week_off, for today only.
    string    TodayStatus,
    long?     TodayAttendanceId,

    AttendanceSummaryResponse           Summary,
    IReadOnlyList<AttendanceDayItem>    Days);

/// <summary>Returned from a mark or a correction, so the client can update the row in place.</summary>
public record AttendanceRecordResponse(
    long            Id,
    long            StaffId,
    DateOnly        Date,
    string          Status,
    bool            IsLate,
    DateTimeOffset? CheckInAt,
    DateTimeOffset? CheckOutAt,
    string          MarkedVia,
    string?         Notes);

// ---------------------------------------------------------------------------
// Queries
// ---------------------------------------------------------------------------

/// <summary>Query string for the roster screen. Bounded on purpose — see PageSize.</summary>
public class AttendanceQuery
{
    /// <summary>The day being viewed. Defaults to today in the branch's timezone.</summary>
    public DateOnly? Date { get; set; }

    [StringLength(80)]
    public string? Search { get; set; }

    /// <summary>trainer | receptionist | manager | admin. Unset means every role.</summary>
    public string? Role { get; set; }

    public long? ShiftId { get; set; }

    /// <summary>present | absent | not_marked | week_off.</summary>
    public string? Status { get; set; }

    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    /// <summary>
    /// Capped at 100. Without a ceiling, ?pageSize=1000000 is a free
    /// denial-of-service and a bulk export of the whole staff roster in one
    /// request.
    /// </summary>
    [Range(1, 100)]
    public int PageSize { get; set; } = 20;
}

/// <summary>Query string for the detail page's Attendance Log.</summary>
public class AttendanceLogQuery
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }

    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    [Range(1, 100)]
    public int PageSize { get; set; } = 20;
}
