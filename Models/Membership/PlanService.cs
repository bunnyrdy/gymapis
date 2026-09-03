namespace GymApis.Models.Membership;

/// <summary>
/// Maps onto `plan_services` — the controlled vocabulary behind the Add Plan
/// screen's "Included Services" grid. Seeded per tenant by 003_plan_services.sql;
/// read-only from the API for now.
/// </summary>
public class PlanService
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public short DisplayOrder { get; set; }
}

/// <summary>
/// Join row for `membership_plan_services` (composite PK, no surrogate id).
/// Named ...Link rather than MembershipPlanService so it does not collide with
/// the service class of that name.
/// </summary>
public class PlanServiceLink
{
    public long PlanId { get; set; }
    public long ServiceId { get; set; }
    public PlanService Service { get; set; } = default!;
}
