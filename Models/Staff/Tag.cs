namespace GymApis.Models.Staff;

/// <summary>
/// Maps onto `tags`. Two categories: 'responsibility' (Check-in, Tours, ...)
/// and 'specialization' (used by the trainer screens). Seeded per tenant.
/// </summary>
public class Tag
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public string Category { get; set; } = default!;
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; } = true;
}

/// <summary>Join row for `staff_tags` (composite PK, no surrogate id).</summary>
public class StaffTag
{
    public long StaffId { get; set; }
    public long TagId { get; set; }
    public Tag Tag { get; set; } = default!;
}
