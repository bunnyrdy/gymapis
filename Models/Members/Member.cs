namespace GymApis.Models.Members;

/// <summary>
/// Maps onto `members`. Personal data only — what someone bought lives on
/// <see cref="Memberships"/>, so a member can renew, upgrade or lapse without
/// losing history.
///
/// The namespace is plural for the same reason `Models/Staff/Staff.cs` declares
/// `StaffMember`: a type may not share its own namespace's name.
///
/// Three columns differ from the staff entity and are easy to get wrong:
///   * the joining date is <see cref="JoinedOn"/>, not JoiningDate, and the DB
///     defaults it to CURRENT_DATE;
///   * <see cref="Status"/> is active | inactive | frozen | banned — the staff
///     vocabulary (on_leave, terminated) fails the CHECK constraint here;
///   * there is a <see cref="CreatedByUserId"/>, recorded from the validated
///     JWT rather than the request body.
///
/// <see cref="UserId"/> stays NULL for the whole of V1; populating it is all
/// the member portal will need.
/// </summary>
public class Member
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long BranchId { get; set; }

    /// <summary>NULL through V1 — reserved for the member portal login.</summary>
    public long? UserId { get; set; }

    /// <summary>Human-facing code, unique per tenant: 'MEM-0004'. Server-generated.</summary>
    public string MemberCode { get; set; } = default!;

    public string FullName { get; set; } = default!;
    public string Phone { get; set; } = default!;
    public string? Email { get; set; }

    /// <summary>male | female | other | undisclosed (DB CHECK constraint).</summary>
    public string? Gender { get; set; }

    public DateOnly? DateOfBirth { get; set; }
    public string? Address { get; set; }
    public string? PhotoUrl { get; set; }

    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }

    /// <summary>The form's "Joining Date". DB default is CURRENT_DATE.</summary>
    public DateOnly JoinedOn { get; set; }

    /// <summary>active | inactive | frozen | banned (DB CHECK constraint).</summary>
    public string Status { get; set; } = "active";

    /// <summary>
    /// DPDP marketing consent. Gates `offer` mail only — a renewal reminder is
    /// a service message about a contract this member signed and needs no
    /// separate consent. Default false, because opt-in means opt-in; it is
    /// bound from an explicit, unticked checkbox on the member form and is
    /// never set server-side.
    /// </summary>
    public bool MarketingOptIn { get; set; }

    public string? Notes { get; set; }

    public long? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Soft delete; a global query filter hides the row. Never a hard delete —
    /// `payments.member_id` is ON DELETE RESTRICT precisely so a member cannot
    /// be removed out from under their own receipts.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public List<Membership> Memberships { get; set; } = [];
}
