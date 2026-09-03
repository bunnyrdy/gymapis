using System.ComponentModel.DataAnnotations;

namespace GymApis.Dtos;

/// <summary>
/// Allowed values, mirroring the CHECK constraints in 008_public_site.sql.
/// Validating here means a bad value is a 400 with a useful message rather than
/// a Postgres constraint violation surfacing as a 500.
/// </summary>
public static class SiteEnums
{
    public static readonly string[] Sections = ["about", "gallery"];
    public static readonly string[] MediaKinds = ["image", "video"];

    /// <summary>
    /// What a single reorder call may carry. Not a guess: it is the ceiling on
    /// how many ids one `PATCH /reorder` will loop over, and without it the
    /// endpoint is an unbounded write in one request.
    /// </summary>
    public const int MaxReorderIds = 200;
}

// ---------------------------------------------------------------------------
// Settings
// ---------------------------------------------------------------------------

/// <summary>
/// The write model for the website settings singleton.
///
/// SECURITY — what is deliberately NOT here: Id, TenantId, and every *Url the
/// upload endpoints own. Artwork is replaced by posting a file to the media
/// endpoints, never by posting a string: a caller who could set
/// `heroImageUrl` could point the gym's homepage at any URL on the internet.
///
/// Nor are gym name, tagline, logo, phone, email or address here. They live on
/// `tenants` and `branches` and are edited on the admin Settings screen — see
/// the banner in 008_public_site.sql for why they are not copied.
/// </summary>
public class SiteSettingsWriteRequest : IValidatableObject
{
    [StringLength(120)]
    public string? HeroHeadline { get; set; }

    [StringLength(400)]
    public string? HeroSubtext { get; set; }

    [Range(1900, 2200)]
    public short? FoundedYear { get; set; }

    /// <summary>
    /// Null — the normal case — means the hero counts active members live.
    /// Set it only when the real figure is not the one to publish.
    /// </summary>
    [Range(0, 1_000_000)]
    public int? MemberCountOverride { get; set; }

    [StringLength(160)]
    public string? AboutTitle { get; set; }

    [StringLength(4000)]
    public string? AboutDescription { get; set; }

    [StringLength(300)]
    public string? InstagramUrl { get; set; }

    [StringLength(20)]
    public string? WhatsappNumber { get; set; }

    [StringLength(600)]
    public string? MapsUrl { get; set; }

    [StringLength(300)]
    public string? FooterText { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        // These three go straight into an href on a page anyone can visit. A
        // `javascript:` or `data:` URL there is stored XSS, so the scheme is
        // checked on the way in as well as escaped on the way out.
        foreach (var r in RequireHttpUrl(InstagramUrl, nameof(InstagramUrl))) yield return r;
        foreach (var r in RequireHttpUrl(MapsUrl, nameof(MapsUrl))) yield return r;

        if (!string.IsNullOrWhiteSpace(WhatsappNumber) &&
            !System.Text.RegularExpressions.Regex.IsMatch(WhatsappNumber, @"^\+?[0-9\s\-]{6,20}$"))
            yield return new ValidationResult(
                "WhatsApp number may contain only digits, spaces, hyphens and a leading +.",
                [nameof(WhatsappNumber)]);
    }

    internal static IEnumerable<ValidationResult> RequireHttpUrl(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            yield return new ValidationResult(
                "Enter a full web address starting with http:// or https://.", [field]);
    }
}

/// <summary>What the CMS settings screen reads back. Includes the server-owned URLs.</summary>
public record SiteSettingsResponse(
    string? HeroHeadline,
    string? HeroSubtext,
    string? HeroImageUrl,
    string? HeroVideoUrl,
    short?  FoundedYear,
    int?    MemberCountOverride,
    string? AboutTitle,
    string? AboutDescription,
    string? WebsiteLogoUrl,
    string? LoginImageUrl,
    string? InstagramUrl,
    string? WhatsappNumber,
    string? MapsUrl,
    string? FooterText,
    /// <summary>Read-through from `tenants` / `branches`, shown so the editor can see
    /// what the site will display without opening another screen. Not writable here.</summary>
    SiteIdentityResponse Identity);

/// <summary>
/// The fields the website shows but this module does not own: brand from
/// `tenants`, contact from `branches`. Surfaced read-only so there is exactly
/// one place each value is stored and one screen that edits it.
/// </summary>
public record SiteIdentityResponse(
    string  GymName,
    string? Tagline,
    string? LogoUrl,
    string? Phone,
    string? Email,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State);

// ---------------------------------------------------------------------------
// Media (About collage + Gallery)
// ---------------------------------------------------------------------------

/// <summary>
/// Metadata for a media row. The file itself arrives as multipart on the same
/// request — the URL is never client-supplied.
/// </summary>
public class SiteMediaWriteRequest : IValidatableObject
{
    public string Section { get; set; } = "gallery";

    [StringLength(200)]
    public string? Caption { get; set; }

    public short DisplayOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (!SiteEnums.Sections.Contains(Section))
            yield return new ValidationResult(
                $"Section must be one of: {string.Join(", ", SiteEnums.Sections)}.",
                [nameof(Section)]);
    }
}

public record SiteMediaResponse(
    long    Id,
    string  Section,
    string  Kind,
    string  Url,
    string? PosterUrl,
    string? Caption,
    short   DisplayOrder,
    bool    IsActive);

// ---------------------------------------------------------------------------
// Offers
// ---------------------------------------------------------------------------

public class SiteOfferWriteRequest
{
    [Required, StringLength(120, MinimumLength = 2)]
    public string Title { get; set; } = default!;

    /// <summary>The big number on the card: '10% OFF', 'FREE'.</summary>
    [StringLength(40)]
    public string? ValueLabel { get; set; }

    [StringLength(500)]
    public string? Description { get; set; }

    public short DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public record SiteOfferResponse(
    long    Id,
    string  Title,
    string? ValueLabel,
    string? Description,
    short   DisplayOrder,
    bool    IsActive);

// ---------------------------------------------------------------------------
// Transformations
// ---------------------------------------------------------------------------

/// <summary>
/// The write model for a success story.
///
/// DPDP: <see cref="ConsentGiven"/> is a boolean the editor ticks, not a
/// timestamp the client supplies. The server stamps `consent_given_at` and
/// records which staff account did it from the validated JWT — a caller must
/// not be able to back-date a consent that was never taken.
///
/// The before/after images arrive as multipart on the same request. There is no
/// url field for the same reason as the settings artwork.
/// </summary>
public class SiteTransformationWriteRequest : IValidatableObject
{
    /// <summary>Optional link to the member this story is about.</summary>
    public long? MemberId { get; set; }

    /// <summary>The name as the member agreed it may appear publicly.</summary>
    [Required, StringLength(120, MinimumLength = 2)]
    public string DisplayName { get; set; } = default!;

    [StringLength(80)]
    public string? Goal { get; set; }

    [StringLength(120)]
    public string? Achievement { get; set; }

    [StringLength(60)]
    public string? DurationLabel { get; set; }

    [StringLength(1500)]
    public string? Description { get; set; }

    /// <summary>The editor's "the member has consented to this being published" tick.</summary>
    public bool ConsentGiven { get; set; }

    /// <summary>Visible on the public site. Requires <see cref="ConsentGiven"/>.</summary>
    public bool IsActive { get; set; }

    public short DisplayOrder { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        // The first of the three gates. The service re-checks against the
        // stored row (a story consented last week arrives here with the box
        // already ticked), and the DB CHECK is the backstop.
        if (IsActive && !ConsentGiven)
            yield return new ValidationResult(
                "A transformation cannot be published until the member's consent is recorded.",
                [nameof(ConsentGiven)]);
    }
}

public record SiteTransformationResponse(
    long    Id,
    long?   MemberId,
    string  DisplayName,
    string? Goal,
    string  BeforeImageUrl,
    string  AfterImageUrl,
    string? Achievement,
    string? DurationLabel,
    string? Description,
    DateTimeOffset? ConsentGivenAt,
    short   DisplayOrder,
    bool    IsActive);

// ---------------------------------------------------------------------------
// Events
// ---------------------------------------------------------------------------

public class SiteEventWriteRequest : IValidatableObject
{
    [Required, StringLength(160, MinimumLength = 2)]
    public string Title { get; set; } = default!;

    [Required]
    public DateOnly EventDate { get; set; }

    public TimeOnly? StartTime { get; set; }

    /// <summary>Null for an open-ended or recurring event.</summary>
    public TimeOnly? EndTime { get; set; }

    [StringLength(160)]
    public string? Location { get; set; }

    [StringLength(1500)]
    public string? Description { get; set; }

    public short DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (StartTime is not null && EndTime is not null && EndTime <= StartTime)
            yield return new ValidationResult(
                "The end time must be after the start time.", [nameof(EndTime)]);

        if (EndTime is not null && StartTime is null)
            yield return new ValidationResult(
                "Set a start time before an end time.", [nameof(StartTime)]);
    }
}

public record SiteEventResponse(
    long      Id,
    string    Title,
    string?   ImageUrl,
    DateOnly  EventDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string?   Location,
    string?   Description,
    short     DisplayOrder,
    bool      IsActive);

// ---------------------------------------------------------------------------
// Shared query + reorder
// ---------------------------------------------------------------------------

/// <summary>Query string for every CMS list. Bounded on purpose — see PageSize.</summary>
public class SiteContentQuery
{
    /// <summary>about | gallery. Media lists only; ignored elsewhere.</summary>
    public string? Section { get; set; }

    /// <summary>active | inactive. Anything else is ignored rather than erroring.</summary>
    public string? Status { get; set; }

    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    /// <summary>
    /// Capped at 100. Without a ceiling, ?pageSize=1000000 is a free
    /// denial-of-service and a bulk export in one request.
    /// </summary>
    [Range(1, 100)]
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// The whole ordered run, not a pair of swapped ids. Sending the final order
/// makes the write idempotent and means two editors reordering at once end with
/// one of the two orders rather than an interleaved third.
/// </summary>
public class ReorderRequest : IValidatableObject
{
    [Required]
    public long[] OrderedIds { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (OrderedIds.Length == 0)
            yield return new ValidationResult("Send at least one id.", [nameof(OrderedIds)]);

        if (OrderedIds.Length > SiteEnums.MaxReorderIds)
            yield return new ValidationResult(
                $"A reorder cannot carry more than {SiteEnums.MaxReorderIds} items.",
                [nameof(OrderedIds)]);

        if (OrderedIds.Length != OrderedIds.Distinct().Count())
            yield return new ValidationResult(
                "The same item appears twice in the order.", [nameof(OrderedIds)]);
    }
}

// ---------------------------------------------------------------------------
// The public payloads
//
// These are a separate set of records from the CMS responses above, and that is
// the point rather than duplication: the public shapes carry no ids that link
// back to a member, no consent timestamps, no is_active flags and no internal
// plan codes. Reusing the admin DTO on an anonymous endpoint is how those leak.
// ---------------------------------------------------------------------------

public record PublicMediaItem(string Kind, string Url, string? PosterUrl, string? Caption);

public record PublicOffer(string Title, string? ValueLabel, string? Description);

public record PublicPlan(
    long    Id,
    string  Name,
    string? Description,
    int     DurationValue,
    string  DurationUnit,
    decimal Price,
    bool    IsFeatured,
    IReadOnlyList<string> Services);

public record PublicTransformation(
    string  DisplayName,
    string? Goal,
    string  BeforeImageUrl,
    string  AfterImageUrl,
    string? Achievement,
    string? DurationLabel,
    string? Description);

public record PublicEvent(
    string    Title,
    string?   ImageUrl,
    DateOnly  EventDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string?   Location,
    string?   Description);

/// <summary>The hero block, with every fallback already resolved server-side.</summary>
public record PublicHero(
    string  Headline,
    string? Subtext,
    string? ImageUrl,
    string? VideoUrl,
    int     ActiveMembers,
    int?    YearsOfExperience);

public record PublicContact(
    string? Phone,
    string? Email,
    string? InstagramUrl,
    string? WhatsappNumber,
    string? MapsUrl,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State);

/// <summary>
/// Everything the marketing site needs to paint, in one response.
///
/// One endpoint rather than five for the same reason the dashboard has one: the
/// cards, the plans and the counters all describe the same instant, and five
/// round trips on a phone connection is five chances to render a half-built
/// page. The two genuinely unbounded lists (transformations, events) ship their
/// first page here and have their own paged endpoints for the rest.
/// </summary>
public record PublicSiteResponse(
    string  GymName,
    string? Tagline,
    string? LogoUrl,
    string? FooterText,
    PublicHero Hero,
    string? AboutTitle,
    string? AboutDescription,
    IReadOnlyList<PublicMediaItem> AboutMedia,
    IReadOnlyList<PublicMediaItem> Gallery,
    IReadOnlyList<PublicPlan> Plans,
    IReadOnlyList<PublicOffer> Offers,
    IReadOnlyList<PublicTransformation> Transformations,
    IReadOnlyList<PublicEvent> Events,
    PublicContact Contact);

/// <summary>Query string for the paged public lists. Same ceiling as the CMS.</summary>
public class PublicPageQuery
{
    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    [Range(1, 60)]
    public int PageSize { get; set; } = 12;
}
