using System.ComponentModel.DataAnnotations;

namespace GymApis.Dtos;

/// <summary>
/// Allowed values, mirroring the CHECK constraints in schema_v1.sql. Validating
/// here means a bad value returns 400 with a useful message instead of letting
/// Postgres raise a constraint violation that surfaces as a 500.
/// </summary>
public static class StaffEnums
{
    public static readonly string[] Genders = ["male", "female", "other", "undisclosed"];
    public static readonly string[] Statuses = ["active", "inactive", "on_leave", "terminated"];
}

/// <summary>
/// The write model shared by every `staff` role. Receptionists and trainers sit
/// in the same table and differ only by `role`, so they share one contract; each
/// form sends the subset its design shows.
///
/// SECURITY — what is deliberately NOT here: Id, TenantId, BranchId, StaffCode,
/// UserId and Role. Those are set by the server. Binding straight to the entity
/// would let a caller post {"role":"admin"} or {"tenantId":2} and either
/// escalate their own privileges or write into another tenant. The DTO is the
/// allow-list.
/// </summary>
public class StaffWriteRequest : IValidatableObject
{
    // ---- Personal ----------------------------------------------------------
    [Required, StringLength(120, MinimumLength = 2)]
    public string FullName { get; set; } = default!;

    public string? Gender { get; set; }

    public DateOnly? DateOfBirth { get; set; }

    [Required]
    [RegularExpression(@"^\d{10}$", ErrorMessage = "Phone number must contain exactly 10 digits.")]
    public string Phone { get; set; } = default!;

    [EmailAddress, StringLength(160)]
    public string? Email { get; set; }

    [StringLength(300)]
    public string? Address { get; set; }

    [StringLength(120)]
    public string? EmergencyContactName { get; set; }

    
    [RegularExpression(@"^\d{10}$", ErrorMessage = "Phone number must contain exactly 10 digits.")]
    public string? EmergencyContactPhone { get; set; }

    // ---- Professional ------------------------------------------------------
    [StringLength(80)]
    public string? JobTitle { get; set; }

    /// <summary>Trainers only — one of the tenant's 'specialization' tags.</summary>
    [StringLength(80)]
    public string? Specialization { get; set; }

    /// <summary>Trainers only. The form sends a comma-separated string; this is the parsed array.</summary>
    public string[] Qualifications { get; set; } = [];

    [Range(0, 60)]
    public decimal? ExperienceYears { get; set; }

    [Required]
    public DateOnly JoiningDate { get; set; }

    public string Status { get; set; } = "active";

    [StringLength(1000)]
    public string? Notes { get; set; }

    // ---- Shift -------------------------------------------------------------
    public long? ShiftId { get; set; }
    public TimeOnly? CustomStartTime { get; set; }
    public TimeOnly? CustomEndTime { get; set; }

    /// <summary>ISO weekdays, 1=Mon .. 7=Sun.</summary>
    public short[] WorkingDays { get; set; } = [];

    // ---- Responsibilities (receptionists) ----------------------------------
    public long[] ResponsibilityTagIds { get; set; } = [];

    /// <summary>
    /// Cross-field rules that attributes cannot express. Runs automatically
    /// because [ApiController] validates the model before the action executes.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (Gender is not null && !StaffEnums.Genders.Contains(Gender))
            yield return new($"Gender must be one of: {string.Join(", ", StaffEnums.Genders)}.", [nameof(Gender)]);

        if (!StaffEnums.Statuses.Contains(Status))
            yield return new($"Status must be one of: {string.Join(", ", StaffEnums.Statuses)}.", [nameof(Status)]);

        if (DateOfBirth is { } dob)
        {
            if (dob >= today)
                yield return new("Date of birth must be in the past.", [nameof(DateOfBirth)]);
            // Guards against a typo'd year producing a 3-year-old or a 130-year-old employee.
            else if (dob > today.AddYears(-16))
                yield return new("Staff must be at least 16 years old.", [nameof(DateOfBirth)]);
            else if (dob < today.AddYears(-100))
                yield return new("Date of birth looks incorrect.", [nameof(DateOfBirth)]);
        }

        if (JoiningDate > today.AddYears(1))
            yield return new("Joining date cannot be more than a year in the future.", [nameof(JoiningDate)]);
        if (JoiningDate < today.AddYears(-60))
            yield return new("Joining date looks incorrect.", [nameof(JoiningDate)]);
        if (DateOfBirth is { } birth && JoiningDate < birth.AddYears(16))
            yield return new("Joining date is before this person turned 16.", [nameof(JoiningDate)]);

        // The DB CHECK is `working_days <@ ARRAY[1..7]`; catch it here so the
        // caller gets a field-level message rather than a constraint violation.
        if (WorkingDays.Any(d => d is < 1 or > 7))
            yield return new("Working days must be 1 (Mon) through 7 (Sun).", [nameof(WorkingDays)]);
        if (WorkingDays.Length != WorkingDays.Distinct().Count())
            yield return new("Working days contains duplicates.", [nameof(WorkingDays)]);

        if (ShiftId is not null && WorkingDays.Length == 0)
            yield return new("Select at least one working day for the shift.", [nameof(WorkingDays)]);
        if (ShiftId is null && WorkingDays.Length > 0)
            yield return new("Select a shift for the chosen working days.", [nameof(ShiftId)]);

        if (CustomStartTime is not null != (CustomEndTime is not null))
            yield return new("Provide both a start and an end time, or neither.", [nameof(CustomStartTime)]);
        if (CustomStartTime is { } s && CustomEndTime is { } e && e <= s)
            yield return new("End time must be after start time.", [nameof(CustomEndTime)]);

        // A silently truncated 200-item array would be worse than a clear error.
        if (ResponsibilityTagIds.Length > 20)
            yield return new("Too many responsibilities selected.", [nameof(ResponsibilityTagIds)]);

        if (Qualifications.Length > 20)
            yield return new("Too many qualifications listed.", [nameof(Qualifications)]);
        if (Qualifications.Any(q => q.Length > 80))
            yield return new("Each qualification must be 80 characters or fewer.", [nameof(Qualifications)]);
    }
}

// ---------------------------------------------------------------------------
// Read models
// ---------------------------------------------------------------------------

public record ShiftAssignmentResponse(
    long      ShiftId,
    string    ShiftName,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    short[]   WorkingDays);

public record TagResponse(long Id, string Name);

public record StaffResponse(
    long      Id,
    string    StaffCode,
    string    Role,
    string    FullName,
    string?   Gender,
    DateOnly? DateOfBirth,
    string      Phone,
    string?   Email,
    string?   Address,
    string?   PhotoUrl,
    string?   EmergencyContactName,
    string?     EmergencyContactPhone,
    string?   JobTitle,
    string?   Specialization,
    string[]  Qualifications,
    decimal?  ExperienceYears,
    DateOnly  JoiningDate,
    string    Status,
    string?   Notes,
    int       PtClientCount,
    ShiftAssignmentResponse? Shift,
    IReadOnlyList<TagResponse> Responsibilities);

/// <summary>Trimmed shape for the table — no notes, no address, no emergency contact.</summary>
/// <summary>
/// The cards above a staff list. Counted over the whole branch for this role,
/// not over the page on screen — a card that changes when you paginate is
/// worse than no card.
///
/// One shape for both roles. WithPtClients is only meaningful for trainers and
/// comes back 0 for receptionists; a second DTO to express that would be two
/// endpoints, two hooks and two tests for one integer.
/// </summary>
public record StaffStatsResponse(
    int Total,
    int Active,
    int Inactive,
    int OnLeave,
    int WithPtClients);

public record StaffListItem(
    long     Id,
    string   StaffCode,
    string   FullName,
    string?  Gender,
    string   Phone,
    string?  Email,
    string?  PhotoUrl,
    string?  Specialization,
    decimal? ExperienceYears,
    string   Status,
    string?  ShiftName,
    TimeOnly? ShiftStart,
    TimeOnly? ShiftEnd,
    int      PtClientCount,
    IReadOnlyList<string> Responsibilities);

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>Query string for the list screens. Bounded on purpose — see PageSize.</summary>
public class StaffQuery
{
    [StringLength(80)]
    public string? Search { get; set; }

    public string? Status { get; set; }
    public string? Gender { get; set; }
    public long? ShiftId { get; set; }

    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    /// <summary>
    /// Capped at 100. Without a ceiling, ?pageSize=1000000 is a free
    /// denial-of-service and a bulk data export in one request.
    /// </summary>
    [Range(1, 100)]
    public int PageSize { get; set; } = 20;
}
