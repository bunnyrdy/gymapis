namespace GymApis.Models.Site;

/// <summary>
/// Maps onto `site_media`. One table serves the About collage and the Gallery,
/// because they are the same row with a different <see cref="Section"/>.
///
/// <see cref="IsActive"/> here is an editor's visibility toggle, not a soft
/// delete — the CMS lists hidden rows and the public projection filters them.
/// Removing a photo really removes it; see the note in 008_public_site.sql.
/// </summary>
public class SiteMedia
{
    public long Id { get; set; }
    public long TenantId { get; set; }

    /// <summary>about | gallery (DB CHECK constraint).</summary>
    public string Section { get; set; } = default!;

    /// <summary>image | video (DB CHECK constraint).</summary>
    public string Kind { get; set; } = default!;

    public string Url { get; set; } = default!;

    /// <summary>
    /// The still a &lt;video&gt; shows before play. Without one a phone either
    /// renders black or downloads the first frame of every video on the page.
    /// </summary>
    public string? PosterUrl { get; set; }

    public string? Caption { get; set; }

    public short DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
