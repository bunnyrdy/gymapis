namespace GymApis.Models.Staff;

/// <summary>
/// Maps onto `shifts`. Seeded: Morning, Mid-day, Evening, Flex.
/// A shift with NULL start/end (Flex) means the times come from the assignment.
/// </summary>
public class Shift
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public string Name { get; set; } = default!;
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Maps onto `staff_shift_assignments`. Dated rows, not a column on `staff`:
/// moving someone from Morning to Evening must not silently rewrite what last
/// month's attendance log claims. `effective_to IS NULL` = the current one, and
/// a partial unique index enforces at most one of those per staff member.
/// </summary>
public class StaffShiftAssignment
{
    public long Id { get; set; }
    public long StaffId { get; set; }
    public long ShiftId { get; set; }

    /// <summary>ISO weekdays, 1=Mon .. 7=Sun. Matches the M T W T F S S toggles.</summary>
    public short[] WorkingDays { get; set; } = [];

    public TimeOnly? CustomStartTime { get; set; }
    public TimeOnly? CustomEndTime { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Shift Shift { get; set; } = default!;
}
