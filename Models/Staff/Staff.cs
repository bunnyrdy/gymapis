namespace GymApis.Models.Staff;

/// <summary>
/// Maps onto the `staff` table. Trainers, receptionists, managers and admins
/// all live here — `role` discriminates. The receptionist screens filter on it.
/// </summary>
public class StaffMember
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long BranchId { get; set; }

    /// <summary>Set only when the person is granted portal access. Null for now.</summary>
    public long? UserId { get; set; }

    /// <summary>Human-facing code, unique per tenant: 'REC-0004'. Server-generated.</summary>
    public string StaffCode { get; set; } = default!;

    /// <summary>trainer | receptionist | manager | admin (DB CHECK constraint).</summary>
    public string Role { get; set; } = default!;

    // Personal
    public string FullName { get; set; } = default!;
    public string? Gender { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public string Phone { get; set; } = default!;
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? PhotoUrl { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }

    // Professional
    public string? JobTitle { get; set; }
    public string? Specialization { get; set; }
    public string[] Qualifications { get; set; } = [];
    public decimal? ExperienceYears { get; set; }
    public DateOnly JoiningDate { get; set; }

    /// <summary>active | inactive | on_leave | terminated (DB CHECK constraint).</summary>
    public string Status { get; set; } = "active";

    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Soft delete. Never hard-delete anyone with attendance or payment history.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public List<StaffTag> StaffTags { get; set; } = [];
    public List<StaffShiftAssignment> ShiftAssignments { get; set; } = [];
}
