namespace GymApis.Models.Site;

/// <summary>
/// Maps onto `site_settings` — one row per tenant, seeded by 008 so every read
/// path can assume it exists.
///
/// What is NOT here is the point of the table. The gym's name, tagline and logo
/// live on `tenants`; its phone, email and address live on `branches`. This row
/// holds only the fields that exist *because* there is a public website. See
/// the banner at the top of 008_public_site.sql.
///
/// Almost everything is nullable: a gym that never opens the CMS still gets a
/// correct site, because the public projection falls back to the tenant/branch
/// rows and to computed figures.
/// </summary>
public class SiteSettings
{
    public long Id { get; set; }
    public long TenantId { get; set; }

    /// <summary>Falls back to `tenants.name` when null.</summary>
    public string? HeroHeadline { get; set; }

    /// <summary>Falls back to `tenants.tagline` when null.</summary>
    public string? HeroSubtext { get; set; }

    public string? HeroImageUrl { get; set; }
    public string? HeroVideoUrl { get; set; }

    /// <summary>
    /// Stored instead of "years of experience" so the number is still right
    /// next January. The public projection subtracts it from the branch's
    /// current year.
    /// </summary>
    public short? FoundedYear { get; set; }

    /// <summary>
    /// NULL — the normal case — means the hero counts active members live from
    /// `v_member_overview`, so the figure can never go stale. Set it only when
    /// the real number is not the one the gym wants to publish.
    /// </summary>
    public int? MemberCountOverride { get; set; }

    public string? AboutTitle { get; set; }
    public string? AboutDescription { get; set; }

    /// <summary>Web-sized derivative. The gym's own logo is `tenants.logo_url`.</summary>
    public string? WebsiteLogoUrl { get; set; }

    /// <summary>The photo beside the sign-in form.</summary>
    public string? LoginImageUrl { get; set; }

    // The three contact channels with no home on `branches`.
    public string? InstagramUrl { get; set; }
    public string? WhatsappNumber { get; set; }
    public string? MapsUrl { get; set; }

    public string? FooterText { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
