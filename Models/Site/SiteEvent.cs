namespace GymApis.Models.Site;

/// <summary>
/// Maps onto `site_events` — upcoming classes, challenges and workshops.
///
/// The date and times are wall-clock facts about the branch's calendar, not an
/// instant: "6:00 AM at the Downtown studio" means 6 AM there, whoever is
/// reading and wherever they are. Storing a timestamptz would make the listed
/// time drift with the visitor's timezone. Same reasoning as IBranchClock in
/// attendance.
/// </summary>
public class SiteEvent
{
    public long Id { get; set; }
    public long TenantId { get; set; }

    public string Title { get; set; } = default!;
    public string? ImageUrl { get; set; }

    public DateOnly EventDate { get; set; }

    public TimeOnly? StartTime { get; set; }

    /// <summary>Nullable — a daily challenge has a start and no end.</summary>
    public TimeOnly? EndTime { get; set; }

    public string? Location { get; set; }
    public string? Description { get; set; }

    public short DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
