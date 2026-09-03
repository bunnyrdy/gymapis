namespace GymApis.Models.Site;

/// <summary>
/// Maps onto `site_transformations` — the before/after success stories.
///
/// DPDP: this is the only entity in the system that publishes a real person's
/// name and body photographs to the open internet. That is a purpose distinct
/// from the membership the member signed up for, so it carries its own consent
/// and its own gate:
///
///   * <see cref="ConsentGivenAt"/> is null until a human ticks the box. The
///     service refuses to publish without it, the public projection filters on
///     it a second time, and a DB CHECK makes the pair impossible to break by
///     accident.
///   * <see cref="ConsentCapturedBy"/> is read from the validated JWT via
///     ICurrentUser, never from the request body.
///   * <see cref="MemberId"/> is nullable so erasing a member cannot fail on a
///     foreign key — but erasure also deletes these rows outright, because
///     leaving the photos up is exactly what erasure is supposed to stop.
///   * <see cref="DisplayName"/> is stored rather than joined so a member can
///     consent to "Rahul S." rather than their full legal name.
/// </summary>
public class SiteTransformation
{
    public long Id { get; set; }
    public long TenantId { get; set; }

    /// <summary>Optional link back to the member. See the note on the class.</summary>
    public long? MemberId { get; set; }

    /// <summary>The name as the member agreed it should appear publicly.</summary>
    public string DisplayName { get; set; } = default!;

    /// <summary>'Muscle Gain', 'Weight Loss'.</summary>
    public string? Goal { get; set; }

    public string BeforeImageUrl { get; set; } = default!;
    public string AfterImageUrl { get; set; } = default!;

    /// <summary>'Gained 8 kg Muscle'.</summary>
    public string? Achievement { get; set; }

    /// <summary>'6 Months'. Free text — the client writes what reads well.</summary>
    public string? DurationLabel { get; set; }

    public string? Description { get; set; }

    /// <summary>Null = not consented = may not be published. See the class note.</summary>
    public DateTimeOffset? ConsentGivenAt { get; set; }

    /// <summary>`users.id` of the staff member who recorded the consent.</summary>
    public long? ConsentCapturedBy { get; set; }

    public short DisplayOrder { get; set; }

    /// <summary>Defaults to false: nothing goes live until someone consents.</summary>
    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
