namespace GymApis.Models.Site;

/// <summary>
/// Maps onto `site_offers` — the promotions strip under the plans.
///
/// No date window on purpose: the requirement is a toggle, and a start/end pair
/// is a second mechanism that can disagree with it ("active, but expired").
/// </summary>
public class SiteOffer
{
    public long Id { get; set; }
    public long TenantId { get; set; }

    public string Title { get; set; } = default!;

    /// <summary>The big number on the card: '10% OFF', 'FREE', '1 MONTH'.</summary>
    public string? ValueLabel { get; set; }

    public string? Description { get; set; }

    public short DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
