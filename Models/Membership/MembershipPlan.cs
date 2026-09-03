namespace GymApis.Models.Membership;

/// <summary>
/// Maps onto `membership_plans`. What the gym sells: a name, a duration, a
/// price and a set of included services.
///
/// Two things differ from the staff entities and are easy to get wrong:
///   * soft delete is <see cref="ArchivedAt"/>, not a DeletedAt column;
///   * <see cref="BranchId"/> is nullable, and NULL means "sold at every
///     branch" — the normal case, and what this module writes.
/// </summary>
public class MembershipPlan
{
    public long Id { get; set; }
    public long TenantId { get; set; }

    /// <summary>NULL = available at every branch. See the note on the class.</summary>
    public long? BranchId { get; set; }

    public string Name { get; set; } = default!;

    /// <summary>Human-facing code, unique per tenant: 'PLN-0004'. Generated when blank.</summary>
    public string? PlanCode { get; set; }

    public string? Description { get; set; }

    public int DurationValue { get; set; }

    /// <summary>day | week | month (DB CHECK constraint).</summary>
    public string DurationUnit { get; set; } = default!;

    public decimal Price { get; set; }

    /// <summary>
    /// Free-text marketing bullets. NOT the Included Services grid — those are
    /// relational, in <see cref="PlanServices"/>. Unused in V1; see 003_plan_services.sql.
    /// </summary>
    public string[] Features { get; set; } = [];

    /// <summary>The form's "Visibility Status" toggle.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The public site's "MOST POPULAR" badge. A marketing decision, so it is
    /// stored rather than derived — deriving it from price or position would
    /// mean reordering the list silently moved the badge.
    /// </summary>
    public bool IsFeatured { get; set; }

    public short DisplayOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Soft delete. Set by Archive; a global query filter hides the row.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    public List<PlanServiceLink> PlanServices { get; set; } = [];
}
