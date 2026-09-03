using GymApis.Dtos;
using GymApis.Models.Site;
using GymApis.Repos;
using GymApis.Services.Attendance;   // IBranchClock
using GymApis.Services.Members;
using GymApis.Services.Membership;
using Microsoft.EntityFrameworkCore;

namespace GymApis.Services.Site;

/// <summary>
/// What an anonymous visitor is allowed to see.
///
/// This is the only service in the codebase that answers to a request with no
/// user behind it, so the rules are stricter than anywhere else:
///
///  * EVERY READ FILTERS ON is_active. `site_*` rows are drafts until an editor
///    switches them on, and the CMS deliberately has no query filter (the admin
///    screens must see their own drafts) — which makes filtering here the only
///    thing standing between an unfinished page and the internet.
///  * TRANSFORMATIONS FILTER ON CONSENT AS WELL. The second of the three gates;
///    see SiteService.SaveTransformationAsync.
///  * THE RESPONSE RECORDS CARRY NO INTERNAL FIELDS. No member ids, no consent
///    timestamps, no display orders, no plan codes. That is why the Public*
///    records exist separately from the CMS ones rather than being reused.
///  * IT COUNTS NOTHING ITSELF. The active-member figure is
///    MemberService.StatsAsync and the plans are
///    MembershipPlanService.PublicPlansAsync — the same rule the dashboard
///    follows, so a number on the marketing site cannot disagree with the same
///    number on the admin screen.
///
/// One endpoint composes the whole site for the same reason the dashboard has
/// one: the hero counter, the plans and the offers all describe one instant,
/// and five round trips is five chances to paint a half-built page.
/// </summary>
public class PublicSiteService : IPublicSiteService
{
    /// <summary>
    /// How many of each unbounded list ride in the aggregate response. The rest
    /// are behind the paged endpoints — a gym with 300 transformations must not
    /// ship all of them to every phone that opens the home page.
    /// </summary>
    private const int PreviewCount = 12;

    private readonly GymDbContext _db;
    private readonly IMemberService _members;
    private readonly IMembershipPlanService _plans;
    private readonly IBranchClock _clock;

    public PublicSiteService(
        GymDbContext db,
        IMemberService members,
        IMembershipPlanService plans,
        IBranchClock clock)
    {
        _db = db;
        _members = members;
        _plans = plans;
        _clock = clock;
    }

    // -----------------------------------------------------------------------
    // SCOPE — tenant, and visible. Both halves, every time.
    // -----------------------------------------------------------------------
    private IQueryable<SiteMedia> VisibleMedia(string section) =>
        _db.SiteMedia
            .AsNoTracking()
            .Where(m => m.TenantId == Tenancy.TenantId && m.Section == section && m.IsActive)
            .OrderBy(m => m.DisplayOrder).ThenBy(m => m.Id);

    private IQueryable<SiteTransformation> VisibleTransformations() =>
        _db.SiteTransformations
            .AsNoTracking()
            .Where(t => t.TenantId == Tenancy.TenantId && t.IsActive && t.ConsentGivenAt != null)
            .OrderBy(t => t.DisplayOrder).ThenBy(t => t.Id);

    /// <summary>
    /// Upcoming only, soonest first. "Today" is the branch's calendar, not
    /// UTC — between midnight and 05:30 IST a UTC-derived date still says
    /// yesterday, which would leave this morning's 6 AM class listed as past.
    /// </summary>
    private async Task<IQueryable<SiteEvent>> VisibleEventsAsync(CancellationToken ct)
    {
        var today = await _clock.TodayAsync(ct);

        return _db.SiteEvents
            .AsNoTracking()
            .Where(e => e.TenantId == Tenancy.TenantId && e.IsActive && e.EventDate >= today)
            .OrderBy(e => e.EventDate).ThenBy(e => e.DisplayOrder);
    }

    // -----------------------------------------------------------------------
    // THE WHOLE SITE
    // -----------------------------------------------------------------------
    public async Task<PublicSiteResponse> GetAsync(CancellationToken ct)
    {
        var settings = await _db.SiteSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == Tenancy.TenantId, ct);

        var branch = await _db.Branches
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == Tenancy.BranchId && b.TenantId == Tenancy.TenantId, ct);

        var brand = await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == Tenancy.TenantId, ct);

        var gymName = brand?.Name ?? branch?.Name ?? "Steel Flex";

        var aboutMedia = await VisibleMedia("about").Select(Project).ToListAsync(ct);
        var gallery = await VisibleMedia("gallery").Take(PreviewCount).Select(Project).ToListAsync(ct);

        var offers = await _db.SiteOffers
            .AsNoTracking()
            .Where(o => o.TenantId == Tenancy.TenantId && o.IsActive)
            .OrderBy(o => o.DisplayOrder).ThenBy(o => o.Id)
            .Select(o => new PublicOffer(o.Title, o.ValueLabel, o.Description))
            .ToListAsync(ct);

        var transformations = await VisibleTransformations()
            .Take(PreviewCount).Select(ProjectTransformation).ToListAsync(ct);

        var events = await (await VisibleEventsAsync(ct))
            .Take(PreviewCount).Select(ProjectEvent).ToListAsync(ct);

        var plans = await _plans.PublicPlansAsync(ct);

        return new PublicSiteResponse(
            gymName,
            brand?.Tagline,
            settings?.WebsiteLogoUrl ?? brand?.LogoUrl,
            settings?.FooterText,
            await HeroAsync(settings, gymName, brand?.Tagline, ct),
            settings?.AboutTitle,
            settings?.AboutDescription,
            aboutMedia,
            gallery,
            plans,
            offers,
            transformations,
            events,
            new PublicContact(
                branch?.Phone,
                branch?.Email,
                settings?.InstagramUrl,
                settings?.WhatsappNumber,
                settings?.MapsUrl,
                branch?.AddressLine1,
                branch?.AddressLine2,
                branch?.City,
                branch?.State));
    }

    /// <summary>
    /// The hero, with every fallback resolved here rather than in the browser.
    ///
    /// Two figures are derived rather than typed, so neither can go stale:
    ///  * the member count comes from MemberService.StatsAsync — the same
    ///    number the admin dashboard shows — unless the owner has set an
    ///    override, which exists because a brand-new gym may not want to
    ///    publish "7";
    ///  * years of experience is this year minus `founded_year`, read off the
    ///    branch clock so it turns over on the gym's new year, not UTC's.
    /// </summary>
    private async Task<PublicHero> HeroAsync(
        SiteSettings? settings, string gymName, string? tagline, CancellationToken ct)
    {
        int activeMembers;
        if (settings?.MemberCountOverride is int given)
        {
            activeMembers = given;
        }
        else
        {
            var stats = await _members.StatsAsync(ct);
            activeMembers = stats.Active;
        }

        int? years = null;
        if (settings?.FoundedYear is short founded)
        {
            var today = await _clock.TodayAsync(ct);
            years = Math.Max(0, today.Year - founded);
        }

        return new PublicHero(
            settings?.HeroHeadline ?? gymName,
            settings?.HeroSubtext ?? tagline,
            settings?.HeroImageUrl,
            settings?.HeroVideoUrl,
            activeMembers,
            years);
    }

    // -----------------------------------------------------------------------
    // THE PAGED LISTS
    // -----------------------------------------------------------------------
    public async Task<PagedResult<PublicMediaItem>> GalleryAsync(PublicPageQuery q, CancellationToken ct)
    {
        var rows = VisibleMedia("gallery");
        var total = await rows.CountAsync(ct);

        var items = await rows
            .Skip((q.Page - 1) * q.PageSize).Take(q.PageSize)
            .Select(Project)
            .ToListAsync(ct);

        return new PagedResult<PublicMediaItem>(items, q.Page, q.PageSize, total);
    }

    public async Task<PagedResult<PublicTransformation>> TransformationsAsync(
        PublicPageQuery q, CancellationToken ct)
    {
        var rows = VisibleTransformations();
        var total = await rows.CountAsync(ct);

        var items = await rows
            .Skip((q.Page - 1) * q.PageSize).Take(q.PageSize)
            .Select(ProjectTransformation)
            .ToListAsync(ct);

        return new PagedResult<PublicTransformation>(items, q.Page, q.PageSize, total);
    }

    public async Task<PagedResult<PublicEvent>> EventsAsync(PublicPageQuery q, CancellationToken ct)
    {
        var rows = await VisibleEventsAsync(ct);
        var total = await rows.CountAsync(ct);

        var items = await rows
            .Skip((q.Page - 1) * q.PageSize).Take(q.PageSize)
            .Select(ProjectEvent)
            .ToListAsync(ct);

        return new PagedResult<PublicEvent>(items, q.Page, q.PageSize, total);
    }

    // -----------------------------------------------------------------------
    // PROJECTIONS
    //
    // Expression-bodied statics so EF translates them into the SELECT list
    // rather than fetching the whole entity and mapping in memory. They are the
    // allow-list: a field that is not written here cannot leave the building.
    // -----------------------------------------------------------------------
    private static readonly System.Linq.Expressions.Expression<Func<SiteMedia, PublicMediaItem>> Project =
        m => new PublicMediaItem(m.Kind, m.Url, m.PosterUrl, m.Caption);

    private static readonly System.Linq.Expressions.Expression<Func<SiteTransformation, PublicTransformation>> ProjectTransformation =
        t => new PublicTransformation(
            t.DisplayName, t.Goal, t.BeforeImageUrl, t.AfterImageUrl,
            t.Achievement, t.DurationLabel, t.Description);

    private static readonly System.Linq.Expressions.Expression<Func<SiteEvent, PublicEvent>> ProjectEvent =
        e => new PublicEvent(
            e.Title, e.ImageUrl, e.EventDate, e.StartTime, e.EndTime, e.Location, e.Description);
}
