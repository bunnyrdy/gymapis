using GymApis.Models.Staff;

namespace GymApis.Models.Attendance;

/// <summary>
/// Maps onto `staff_attendance`. One row per staff member per day, trainers and
/// receptionists in the same table — the roster just filters on `staff.role`.
///
/// A row means "something was expected of this person on this date, and here is
/// what happened". Days off the roster get no row at all; the API reports those
/// as a week off by reading the shift assignment, which keeps this table from
/// filling with entries for days nobody was due in.
///
/// <see cref="ShiftId"/> is a snapshot, not a live lookup. Moving someone from
/// Morning to Evening must not rewrite what last month's log says they worked.
/// </summary>
public class StaffAttendance
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long BranchId { get; set; }
    public long StaffId { get; set; }

    public DateOnly AttendanceDate { get; set; }

    /// <summary>The shift they were rostered on that day, frozen at mark time.</summary>
    public long? ShiftId { get; set; }

    public DateTimeOffset? CheckInAt { get; set; }

    /// <summary>Not captured in V1 — the column is here, the UI renders "--".</summary>
    public DateTimeOffset? CheckOutAt { get; set; }

    /// <summary>
    /// present | absent. The DB CHECK still permits late, half_day, leave,
    /// holiday and week_off, but V1 writes only these two: the spec asks for
    /// two statuses, and lateness rides on <see cref="IsLate"/> instead so that
    /// v_staff_attendance_monthly's arithmetic keeps working unchanged.
    /// </summary>
    public string Status { get; set; } = default!;

    public string? LeaveReason { get; set; }

    /// <summary>Drives the "(Late)" badge. Derived from the shift start, never posted by the client.</summary>
    public bool IsLate { get; set; }

    /// <summary>Null for rows the end-of-day sweep wrote — those have no human actor.</summary>
    public long? MarkedByUserId { get; set; }

    /// <summary>manual | biometric | qr | mobile | system. V1 writes 'manual' or 'system'.</summary>
    public string MarkedVia { get; set; } = "manual";

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Only the shift is navigable. There is deliberately no Staff navigation:
    /// `staff` carries a soft-delete query filter, and a required navigation
    /// into a filtered entity is the case EF warns about — it silently drops
    /// rows. Every query here joins through ScopedStaff() instead, which is
    /// also where the tenant, branch and IDOR guarding live.
    /// </summary>
    public Shift? Shift { get; set; }
}
