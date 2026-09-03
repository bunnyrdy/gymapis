namespace GymApis.Models.Members;

/// <summary>
/// Keyless read model over the `v_member_overview` view.
///
/// The view already does the LEFT JOIN LATERAL that picks each member's latest
/// active-or-expired membership, joins the balance view, derives
/// <see cref="MembershipState"/> from end_date, and filters deleted rows. That
/// is the whole members list in one relation — reimplementing it in LINQ would
/// duplicate logic the schema already owns.
///
/// Keyless means EF will not track or write it. Reads only.
/// </summary>
public class MemberOverview
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long BranchId { get; set; }

    public string MemberCode { get; set; } = default!;
    public string FullName { get; set; } = default!;
    public string Phone { get; set; } = default!;

    /// <summary>The member's own status: active | inactive | frozen | banned.</summary>
    public string MemberStatus { get; set; } = default!;

    public long? MembershipId { get; set; }
    public string? PlanName { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int? DaysRemaining { get; set; }

    /// <summary>no_membership | expired | expiring_soon | active. Derived by the view.</summary>
    public string MembershipState { get; set; } = default!;

    public decimal? PaidAmount { get; set; }
    public decimal? BalanceAmount { get; set; }

    /// <summary>paid | partial | pending. Derived from payments, never stored.</summary>
    public string? PaymentStatus { get; set; }

    // Appended by 004_member_overview.sql for the list's Member/Contact columns
    // and its "All Plans" filter.
    public string? Email { get; set; }
    public string? PhotoUrl { get; set; }
    public long? PlanId { get; set; }

    // Appended by 007_member_overview_joined.sql so the list can answer "who
    // joined this month" from the view rather than joining back to `members`.
    public DateOnly JoinedOn { get; set; }
}
