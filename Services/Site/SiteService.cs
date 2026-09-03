using System.Text.Json;
using GymApis.Dtos;
using GymApis.Models.Common;
using GymApis.Models.Site;
using GymApis.Repos;
using GymApis.Services.Staff;   // ServiceResult<T>
using GymApis.Services.Storage;
using Microsoft.EntityFrameworkCore;

namespace GymApis.Services.Site;

/// <summary>
/// The CMS behind the public website. Same shape as MembershipPlanService: one
/// scoping helper, DTO allow-lists, an audit row for every mutation.
///
/// Three things are specific to this module and worth knowing before editing:
///
///  * ARTWORK URLS ARE NEVER CLIENT-SUPPLIED. Every image and video arrives as
///    a file and leaves as a GUID path from PhotoStorage. No write DTO in this
///    module has a *Url property, because a caller who could set one could
///    point the gym's homepage at any URL on the internet — or at a path on
///    this disk.
///  * REPLACING ARTWORK DELETES THE OLD FILE. Otherwise every edit of the hero
///    leaves another orphan in wwwroot until the disk fills.
///  * CONSENT GATES PUBLICATION. See SaveTransformationAsync.
/// </summary>
public class SiteService : ISiteService
{
    private readonly GymDbContext _db;
    private readonly ICurrentUser _actor;
    private readonly IPhotoStorage _photos;

    public SiteService(GymDbContext db, ICurrentUser actor, IPhotoStorage photos)
    {
        _db = db;
        _actor = actor;
        _photos = photos;
    }

    // -----------------------------------------------------------------------
    // SCOPE
    //
    // One helper per table, all saying the same thing. These are the IDOR
    // guards: another tenant's row id simply does not resolve.
    // -----------------------------------------------------------------------
    private IQueryable<SiteMedia> ScopedMedia() =>
        _db.SiteMedia.Where(m => m.TenantId == Tenancy.TenantId);

    private IQueryable<SiteOffer> ScopedOffers() =>
        _db.SiteOffers.Where(o => o.TenantId == Tenancy.TenantId);

    private IQueryable<SiteTransformation> ScopedTransformations() =>
        _db.SiteTransformations.Where(t => t.TenantId == Tenancy.TenantId);

    private IQueryable<SiteEvent> ScopedEvents() =>
        _db.SiteEvents.Where(e => e.TenantId == Tenancy.TenantId);

    // =======================================================================
    // SETTINGS
    // =======================================================================

    /// <summary>
    /// 008 seeds the row, so this should always find one. It creates a blank
    /// one rather than throwing if it does not: a missing settings row is a
    /// deployment mistake, and failing the whole admin screen over it helps
    /// nobody.
    /// </summary>
    private async Task<SiteSettings> SettingsRowAsync(CancellationToken ct)
    {
        var row = await _db.SiteSettings
            .FirstOrDefaultAsync(s => s.TenantId == Tenancy.TenantId, ct);

        if (row is not null) return row;

        row = new SiteSettings { TenantId = Tenancy.TenantId };
        _db.SiteSettings.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<SiteSettingsResponse> GetSettingsAsync(CancellationToken ct)
    {
        var row = await SettingsRowAsync(ct);
        return ToResponse(row, await IdentityAsync(ct));
    }

    public async Task<ServiceResult<SiteSettingsResponse>> UpdateSettingsAsync(
        SiteSettingsWriteRequest req, CancellationToken ct)
    {
        var row = await SettingsRowAsync(ct);

        row.HeroHeadline = Clean(req.HeroHeadline);
        row.HeroSubtext = Clean(req.HeroSubtext);
        row.FoundedYear = req.FoundedYear;
        row.MemberCountOverride = req.MemberCountOverride;
        row.AboutTitle = Clean(req.AboutTitle);
        row.AboutDescription = Clean(req.AboutDescription);
        row.InstagramUrl = Clean(req.InstagramUrl);
        row.WhatsappNumber = Clean(req.WhatsappNumber);
        row.MapsUrl = Clean(req.MapsUrl);
        row.FooterText = Clean(req.FooterText);

        Audit("site.settings.updated", "site_settings", row.Id, "Updated website settings.",
            new { row.HeroHeadline, row.FoundedYear });

        await _db.SaveChangesAsync(ct);
        return ServiceResult<SiteSettingsResponse>.Ok(ToResponse(row, await IdentityAsync(ct)));
    }

    /// <summary>
    /// The four artwork slots. A closed set, mapped inside the service, so the
    /// caller never names a column — the same property the PhotoFolder enum
    /// gives the filesystem path.
    /// </summary>
    public async Task<ServiceResult<SiteSettingsResponse>> SetArtworkAsync(
        string slot, IFormFile? file, bool remove, CancellationToken ct)
    {
        var row = await SettingsRowAsync(ct);

        var isVideo = slot == "heroVideo";
        Func<string?> read = slot switch
        {
            "hero"      => () => row.HeroImageUrl,
            "heroVideo" => () => row.HeroVideoUrl,
            "logo"      => () => row.WebsiteLogoUrl,
            "login"     => () => row.LoginImageUrl,
            _ => () => null,
        };
        Action<string?> write = slot switch
        {
            "hero"      => v => row.HeroImageUrl = v,
            "heroVideo" => v => row.HeroVideoUrl = v,
            "logo"      => v => row.WebsiteLogoUrl = v,
            "login"     => v => row.LoginImageUrl = v,
            _ => _ => { },
        };

        if (slot is not ("hero" or "heroVideo" or "logo" or "login"))
            return ServiceResult<SiteSettingsResponse>.Fail("Unknown artwork slot.");

        var previous = read();

        if (remove)
        {
            _photos.Delete(previous, PhotoFolder.Site);
            write(null);
        }
        else
        {
            if (file is null)
                return ServiceResult<SiteSettingsResponse>.Fail("No file was uploaded.");

            var saved = isVideo
                ? await _photos.SaveVideoAsync(file, PhotoFolder.Site, ct)
                : await _photos.SaveAsync(file, PhotoFolder.Site, ct);

            if (!saved.Succeeded)
                return ServiceResult<SiteSettingsResponse>.Fail(saved.Error!);

            // Only after the new file is safely on disk — a failed upload must
            // not leave the site with no hero at all.
            _photos.Delete(previous, PhotoFolder.Site);
            write(saved.Url);
        }

        Audit("site.artwork.updated", "site_settings", row.Id,
            $"{(remove ? "Removed" : "Replaced")} website {slot} artwork.", new { slot });

        await _db.SaveChangesAsync(ct);
        return ServiceResult<SiteSettingsResponse>.Ok(ToResponse(row, await IdentityAsync(ct)));
    }

    /// <summary>
    /// Brand from `tenants`, contact from `branches`. Read-through, never
    /// copied — see the banner in 008_public_site.sql.
    ///
    /// </summary>
    private async Task<SiteIdentityResponse> IdentityAsync(CancellationToken ct)
    {
        var branch = await _db.Branches
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == Tenancy.BranchId && b.TenantId == Tenancy.TenantId, ct);

        var tenant = await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == Tenancy.TenantId, ct);

        return new SiteIdentityResponse(
            tenant?.Name ?? branch?.Name ?? "Steel Flex",
            tenant?.Tagline,
            tenant?.LogoUrl,
            branch?.Phone,
            branch?.Email,
            branch?.AddressLine1,
            branch?.AddressLine2,
            branch?.City,
            branch?.State);
    }

    private static SiteSettingsResponse ToResponse(SiteSettings s, SiteIdentityResponse identity) => new(
        s.HeroHeadline, s.HeroSubtext, s.HeroImageUrl, s.HeroVideoUrl,
        s.FoundedYear, s.MemberCountOverride,
        s.AboutTitle, s.AboutDescription,
        s.WebsiteLogoUrl, s.LoginImageUrl,
        s.InstagramUrl, s.WhatsappNumber, s.MapsUrl, s.FooterText,
        identity);

    // =======================================================================
    // MEDIA
    // =======================================================================

    public async Task<PagedResult<SiteMediaResponse>> ListMediaAsync(SiteContentQuery q, CancellationToken ct)
    {
        var rows = ScopedMedia();

        if (SiteEnums.Sections.Contains(q.Section)) rows = rows.Where(m => m.Section == q.Section);
        if (q.Status is "active") rows = rows.Where(m => m.IsActive);
        else if (q.Status is "inactive") rows = rows.Where(m => !m.IsActive);

        var total = await rows.CountAsync(ct);

        var items = await rows
            .OrderBy(m => m.DisplayOrder).ThenBy(m => m.Id)
            .Skip((q.Page - 1) * q.PageSize).Take(q.PageSize)
            .Select(m => new SiteMediaResponse(
                m.Id, m.Section, m.Kind, m.Url, m.PosterUrl, m.Caption, m.DisplayOrder, m.IsActive))
            .ToListAsync(ct);

        return new PagedResult<SiteMediaResponse>(items, q.Page, q.PageSize, total);
    }

    /// <summary>
    /// The kind is decided by what the file turns out to be, not by what the
    /// caller says it is: PhotoStorage sniffs the magic bytes, and whichever
    /// save method accepted the file is what the row records.
    /// </summary>
    public async Task<ServiceResult<SiteMediaResponse>> AddMediaAsync(
        SiteMediaWriteRequest req, IFormFile? file, IFormFile? poster, CancellationToken ct)
    {
        if (file is null)
            return ServiceResult<SiteMediaResponse>.Fail("No file was uploaded.");

        // Try image first — that is the overwhelmingly common case — and fall
        // through to video only if the bytes are not one of the three raster
        // formats. Both paths validate independently; neither trusts the
        // Content-Type header.
        var asImage = await _photos.SaveAsync(file, PhotoFolder.Gallery, ct);
        var kind = "image";
        var saved = asImage;

        if (!asImage.Succeeded)
        {
            saved = await _photos.SaveVideoAsync(file, PhotoFolder.Gallery, ct);
            kind = "video";
            if (!saved.Succeeded)
                return ServiceResult<SiteMediaResponse>.Fail(
                    "Upload a JPEG, PNG or WebP image, or an MP4 or WebM video.");
        }

        string? posterUrl = null;
        if (kind == "video" && poster is not null)
        {
            var savedPoster = await _photos.SaveAsync(poster, PhotoFolder.Gallery, ct);
            if (!savedPoster.Succeeded)
            {
                // Do not leave the video orphaned on disk behind a failed row.
                _photos.Delete(saved.Url, PhotoFolder.Gallery);
                return ServiceResult<SiteMediaResponse>.Fail(savedPoster.Error!);
            }
            posterUrl = savedPoster.Url;
        }

        var row = new SiteMedia
        {
            TenantId = Tenancy.TenantId,
            Section = req.Section,
            Kind = kind,
            Url = saved.Url!,
            PosterUrl = posterUrl,
            Caption = Clean(req.Caption),
            DisplayOrder = req.DisplayOrder,
            IsActive = req.IsActive,
        };

        _db.SiteMedia.Add(row);
        await _db.SaveChangesAsync(ct);

        Audit("site.media.added", "site_media", row.Id,
            $"Added {kind} to the {row.Section} gallery.", new { row.Section, row.Kind });
        await _db.SaveChangesAsync(ct);

        return ServiceResult<SiteMediaResponse>.Ok(ToResponse(row));
    }

    /// <summary>Metadata only. The file is replaced by deleting the row and adding another.</summary>
    public async Task<ServiceResult<SiteMediaResponse>> UpdateMediaAsync(
        long id, SiteMediaWriteRequest req, CancellationToken ct)
    {
        var row = await ScopedMedia().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (row is null) return ServiceResult<SiteMediaResponse>.Missing();

        row.Section = req.Section;
        row.Caption = Clean(req.Caption);
        row.DisplayOrder = req.DisplayOrder;
        row.IsActive = req.IsActive;

        Audit("site.media.updated", "site_media", row.Id, "Updated a gallery item.", new { row.Section });
        await _db.SaveChangesAsync(ct);

        return ServiceResult<SiteMediaResponse>.Ok(ToResponse(row));
    }

    public async Task<ServiceResult<bool>> DeleteMediaAsync(long id, CancellationToken ct)
    {
        var row = await ScopedMedia().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (row is null) return ServiceResult<bool>.Missing();

        // A gallery photo is content, not a record of something that happened —
        // there is no ledger to protect, so this is one of the few real deletes
        // in the system. The audit row is what survives.
        _photos.Delete(row.Url, PhotoFolder.Gallery);
        _photos.Delete(row.PosterUrl, PhotoFolder.Gallery);
        _db.SiteMedia.Remove(row);

        Audit("site.media.deleted", "site_media", row.Id,
            $"Removed a {row.Kind} from the {row.Section} gallery.", new { row.Section, row.Kind });
        await _db.SaveChangesAsync(ct);

        return ServiceResult<bool>.Ok(true);
    }

    private static SiteMediaResponse ToResponse(SiteMedia m) => new(
        m.Id, m.Section, m.Kind, m.Url, m.PosterUrl, m.Caption, m.DisplayOrder, m.IsActive);

    // =======================================================================
    // OFFERS
    // =======================================================================

    public async Task<PagedResult<SiteOfferResponse>> ListOffersAsync(SiteContentQuery q, CancellationToken ct)
    {
        var rows = ScopedOffers();

        if (q.Status is "active") rows = rows.Where(o => o.IsActive);
        else if (q.Status is "inactive") rows = rows.Where(o => !o.IsActive);

        var total = await rows.CountAsync(ct);

        var items = await rows
            .OrderBy(o => o.DisplayOrder).ThenBy(o => o.Id)
            .Skip((q.Page - 1) * q.PageSize).Take(q.PageSize)
            .Select(o => new SiteOfferResponse(
                o.Id, o.Title, o.ValueLabel, o.Description, o.DisplayOrder, o.IsActive))
            .ToListAsync(ct);

        return new PagedResult<SiteOfferResponse>(items, q.Page, q.PageSize, total);
    }

    /// <summary>Create when id is null, update otherwise. One method, one set of rules.</summary>
    public async Task<ServiceResult<SiteOfferResponse>> SaveOfferAsync(
        long? id, SiteOfferWriteRequest req, CancellationToken ct)
    {
        SiteOffer row;

        if (id is null)
        {
            row = new SiteOffer { TenantId = Tenancy.TenantId };
            _db.SiteOffers.Add(row);
        }
        else
        {
            var found = await ScopedOffers().FirstOrDefaultAsync(o => o.Id == id, ct);
            if (found is null) return ServiceResult<SiteOfferResponse>.Missing();
            row = found;
        }

        row.Title = req.Title.Trim();
        row.ValueLabel = Clean(req.ValueLabel);
        row.Description = Clean(req.Description);
        row.DisplayOrder = req.DisplayOrder;
        row.IsActive = req.IsActive;

        await _db.SaveChangesAsync(ct);

        Audit(id is null ? "site.offer.created" : "site.offer.updated", "site_offer", row.Id,
            $"{(id is null ? "Created" : "Updated")} offer {row.Title}.",
            new { row.ValueLabel, row.IsActive });
        await _db.SaveChangesAsync(ct);

        return ServiceResult<SiteOfferResponse>.Ok(new SiteOfferResponse(
            row.Id, row.Title, row.ValueLabel, row.Description, row.DisplayOrder, row.IsActive));
    }

    public async Task<ServiceResult<bool>> DeleteOfferAsync(long id, CancellationToken ct)
    {
        var row = await ScopedOffers().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (row is null) return ServiceResult<bool>.Missing();

        _db.SiteOffers.Remove(row);
        Audit("site.offer.deleted", "site_offer", row.Id, $"Deleted offer {row.Title}.", new { row.Title });
        await _db.SaveChangesAsync(ct);

        return ServiceResult<bool>.Ok(true);
    }

    // =======================================================================
    // TRANSFORMATIONS
    // =======================================================================

    public async Task<PagedResult<SiteTransformationResponse>> ListTransformationsAsync(
        SiteContentQuery q, CancellationToken ct)
    {
        var rows = ScopedTransformations();

        if (q.Status is "active") rows = rows.Where(t => t.IsActive);
        else if (q.Status is "inactive") rows = rows.Where(t => !t.IsActive);

        var total = await rows.CountAsync(ct);

        var items = await rows
            .OrderBy(t => t.DisplayOrder).ThenBy(t => t.Id)
            .Skip((q.Page - 1) * q.PageSize).Take(q.PageSize)
            .Select(t => new SiteTransformationResponse(
                t.Id, t.MemberId, t.DisplayName, t.Goal,
                t.BeforeImageUrl, t.AfterImageUrl,
                t.Achievement, t.DurationLabel, t.Description,
                t.ConsentGivenAt, t.DisplayOrder, t.IsActive))
            .ToListAsync(ct);

        return new PagedResult<SiteTransformationResponse>(items, q.Page, q.PageSize, total);
    }

    /// <summary>
    /// DPDP, and the reason this method is longer than its siblings.
    ///
    /// A transformation publishes a real person's name and body photographs.
    /// Three things follow:
    ///
    ///  * Consent is stamped by the server. The DTO carries a boolean the
    ///    editor ticked; `consent_given_at` and `consent_captured_by` are
    ///    written here from the clock and the validated JWT. A client cannot
    ///    back-date a consent that was never taken.
    ///  * Withdrawing consent unpublishes. Un-ticking the box clears the
    ///    timestamp AND forces IsActive false — consent withdrawn while the
    ///    story stays live is the failure this whole mechanism exists to stop.
    ///  * Publication is refused without it, here as well as in the DTO
    ///    validator and the DB CHECK. Three gates, because the story is
    ///    irreversible once it has been on the internet.
    ///
    /// Both images are required on create and optional on update, so an editor
    /// fixing a typo does not have to re-upload two photos.
    /// </summary>
    public async Task<ServiceResult<SiteTransformationResponse>> SaveTransformationAsync(
        long? id, SiteTransformationWriteRequest req,
        IFormFile? before, IFormFile? after, CancellationToken ct)
    {
        SiteTransformation row;

        if (id is null)
        {
            if (before is null || after is null)
                return ServiceResult<SiteTransformationResponse>.Fail(
                    "Both a before and an after photo are required.");

            row = new SiteTransformation { TenantId = Tenancy.TenantId };
        }
        else
        {
            var found = await ScopedTransformations().FirstOrDefaultAsync(t => t.Id == id, ct);
            if (found is null) return ServiceResult<SiteTransformationResponse>.Missing();
            row = found;
        }

        // An id from a request body is never trusted: confirm the member is one
        // of ours before linking a public story to them. The global query
        // filter means an erased member is already invisible here.
        if (req.MemberId is not null)
        {
            var exists = await _db.Members.AnyAsync(
                m => m.Id == req.MemberId &&
                     m.TenantId == Tenancy.TenantId &&
                     m.BranchId == Tenancy.BranchId, ct);

            if (!exists)
                return ServiceResult<SiteTransformationResponse>.Fail("That member was not found.");
        }

        if (before is not null)
        {
            var saved = await _photos.SaveAsync(before, PhotoFolder.Transformation, ct);
            if (!saved.Succeeded) return ServiceResult<SiteTransformationResponse>.Fail(saved.Error!);
            if (id is not null) _photos.Delete(row.BeforeImageUrl, PhotoFolder.Transformation);
            row.BeforeImageUrl = saved.Url!;
        }

        if (after is not null)
        {
            var saved = await _photos.SaveAsync(after, PhotoFolder.Transformation, ct);
            if (!saved.Succeeded) return ServiceResult<SiteTransformationResponse>.Fail(saved.Error!);
            if (id is not null) _photos.Delete(row.AfterImageUrl, PhotoFolder.Transformation);
            row.AfterImageUrl = saved.Url!;
        }

        row.MemberId = req.MemberId;
        row.DisplayName = req.DisplayName.Trim();
        row.Goal = Clean(req.Goal);
        row.Achievement = Clean(req.Achievement);
        row.DurationLabel = Clean(req.DurationLabel);
        row.Description = Clean(req.Description);
        row.DisplayOrder = req.DisplayOrder;

        if (req.ConsentGiven)
        {
            // Stamp once. Re-saving a story that was consented last month must
            // not rewrite the date the consent was actually taken.
            row.ConsentGivenAt ??= DateTimeOffset.UtcNow;
            row.ConsentCapturedBy ??= _actor.UserId;
        }
        else
        {
            row.ConsentGivenAt = null;
            row.ConsentCapturedBy = null;
        }

        // The gate. Publication requires recorded consent, full stop.
        row.IsActive = req.IsActive && row.ConsentGivenAt is not null;

        if (id is null) _db.SiteTransformations.Add(row);
        await _db.SaveChangesAsync(ct);

        Audit(id is null ? "site.transformation.created" : "site.transformation.updated",
            "site_transformation", row.Id,
            $"{(id is null ? "Created" : "Updated")} transformation for {row.DisplayName}.",
            new { row.MemberId, Consented = row.ConsentGivenAt is not null, row.IsActive });
        await _db.SaveChangesAsync(ct);

        return ServiceResult<SiteTransformationResponse>.Ok(ToResponse(row));
    }

    public async Task<ServiceResult<bool>> DeleteTransformationAsync(long id, CancellationToken ct)
    {
        var row = await ScopedTransformations().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (row is null) return ServiceResult<bool>.Missing();

        // A real delete, and deliberately so: this row IS personal data, and a
        // soft delete would leave a member's body photographs on the disk after
        // the gym was asked to take them down.
        _photos.Delete(row.BeforeImageUrl, PhotoFolder.Transformation);
        _photos.Delete(row.AfterImageUrl, PhotoFolder.Transformation);
        _db.SiteTransformations.Remove(row);

        Audit("site.transformation.deleted", "site_transformation", row.Id,
            $"Deleted transformation for {row.DisplayName}.", new { row.MemberId });
        await _db.SaveChangesAsync(ct);

        return ServiceResult<bool>.Ok(true);
    }

    private static SiteTransformationResponse ToResponse(SiteTransformation t) => new(
        t.Id, t.MemberId, t.DisplayName, t.Goal,
        t.BeforeImageUrl, t.AfterImageUrl,
        t.Achievement, t.DurationLabel, t.Description,
        t.ConsentGivenAt, t.DisplayOrder, t.IsActive);

    // =======================================================================
    // EVENTS
    // =======================================================================

    public async Task<PagedResult<SiteEventResponse>> ListEventsAsync(SiteContentQuery q, CancellationToken ct)
    {
        var rows = ScopedEvents();

        if (q.Status is "active") rows = rows.Where(e => e.IsActive);
        else if (q.Status is "inactive") rows = rows.Where(e => !e.IsActive);

        var total = await rows.CountAsync(ct);

        var items = await rows
            .OrderBy(e => e.EventDate).ThenBy(e => e.DisplayOrder)
            .Skip((q.Page - 1) * q.PageSize).Take(q.PageSize)
            .Select(e => new SiteEventResponse(
                e.Id, e.Title, e.ImageUrl, e.EventDate, e.StartTime, e.EndTime,
                e.Location, e.Description, e.DisplayOrder, e.IsActive))
            .ToListAsync(ct);

        return new PagedResult<SiteEventResponse>(items, q.Page, q.PageSize, total);
    }

    public async Task<ServiceResult<SiteEventResponse>> SaveEventAsync(
        long? id, SiteEventWriteRequest req, IFormFile? image, CancellationToken ct)
    {
        SiteEvent row;

        if (id is null)
        {
            row = new SiteEvent { TenantId = Tenancy.TenantId };
        }
        else
        {
            var found = await ScopedEvents().FirstOrDefaultAsync(e => e.Id == id, ct);
            if (found is null) return ServiceResult<SiteEventResponse>.Missing();
            row = found;
        }

        if (image is not null)
        {
            var saved = await _photos.SaveAsync(image, PhotoFolder.Event, ct);
            if (!saved.Succeeded) return ServiceResult<SiteEventResponse>.Fail(saved.Error!);
            if (id is not null) _photos.Delete(row.ImageUrl, PhotoFolder.Event);
            row.ImageUrl = saved.Url;
        }

        row.Title = req.Title.Trim();
        row.EventDate = req.EventDate;
        row.StartTime = req.StartTime;
        row.EndTime = req.EndTime;
        row.Location = Clean(req.Location);
        row.Description = Clean(req.Description);
        row.DisplayOrder = req.DisplayOrder;
        row.IsActive = req.IsActive;

        if (id is null) _db.SiteEvents.Add(row);
        await _db.SaveChangesAsync(ct);

        Audit(id is null ? "site.event.created" : "site.event.updated", "site_event", row.Id,
            $"{(id is null ? "Created" : "Updated")} event {row.Title}.",
            new { row.EventDate, row.IsActive });
        await _db.SaveChangesAsync(ct);

        return ServiceResult<SiteEventResponse>.Ok(new SiteEventResponse(
            row.Id, row.Title, row.ImageUrl, row.EventDate, row.StartTime, row.EndTime,
            row.Location, row.Description, row.DisplayOrder, row.IsActive));
    }

    public async Task<ServiceResult<bool>> DeleteEventAsync(long id, CancellationToken ct)
    {
        var row = await ScopedEvents().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (row is null) return ServiceResult<bool>.Missing();

        _photos.Delete(row.ImageUrl, PhotoFolder.Event);
        _db.SiteEvents.Remove(row);

        Audit("site.event.deleted", "site_event", row.Id, $"Deleted event {row.Title}.", new { row.Title });
        await _db.SaveChangesAsync(ct);

        return ServiceResult<bool>.Ok(true);
    }

    // =======================================================================
    // REORDER
    // =======================================================================

    /// <summary>
    /// One statement for all four content types.
    ///
    /// The request carries the whole ordered run, so the write is idempotent
    /// and two editors reordering at once end with one of their two orders
    /// rather than an interleaved third. Every id is checked against the
    /// tenant's own rows before anything is written — an id that is not ours is
    /// a 400, not a silent no-op, because silently ignoring it would let the
    /// caller probe which ids exist.
    /// </summary>
    public async Task<ServiceResult<bool>> ReorderAsync(string kind, long[] orderedIds, CancellationToken ct)
    {
        var position = orderedIds
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => (short)x.index);

        int matched;

        switch (kind)
        {
            case "media":
            {
                var rows = await ScopedMedia().Where(m => orderedIds.Contains(m.Id)).ToListAsync(ct);
                foreach (var r in rows) r.DisplayOrder = position[r.Id];
                matched = rows.Count;
                break;
            }
            case "offers":
            {
                var rows = await ScopedOffers().Where(o => orderedIds.Contains(o.Id)).ToListAsync(ct);
                foreach (var r in rows) r.DisplayOrder = position[r.Id];
                matched = rows.Count;
                break;
            }
            case "transformations":
            {
                var rows = await ScopedTransformations().Where(t => orderedIds.Contains(t.Id)).ToListAsync(ct);
                foreach (var r in rows) r.DisplayOrder = position[r.Id];
                matched = rows.Count;
                break;
            }
            case "events":
            {
                var rows = await ScopedEvents().Where(e => orderedIds.Contains(e.Id)).ToListAsync(ct);
                foreach (var r in rows) r.DisplayOrder = position[r.Id];
                matched = rows.Count;
                break;
            }
            default:
                return ServiceResult<bool>.Fail("Unknown content type.");
        }

        if (matched != orderedIds.Length)
            return ServiceResult<bool>.Fail("One or more items no longer exist.");

        Audit($"site.{kind}.reordered", $"site_{kind}", null,
            $"Reordered {orderedIds.Length} {kind} items.", new { Count = orderedIds.Length });

        await _db.SaveChangesAsync(ct);
        return ServiceResult<bool>.Ok(true);
    }

    // =======================================================================
    // INTERNALS
    // =======================================================================

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>
    /// Accountability trail. The actor comes from the validated JWT, so it
    /// cannot be forged by the caller. Written for every mutation, and the only
    /// surviving record of a deleted gallery item or event.
    ///
    /// Adds to the change tracker; the caller's SaveChangesAsync flushes it, so
    /// the audit row and the change it describes commit together.
    /// </summary>
    private void Audit(string action, string entityType, long? entityId, string description, object metadata) =>
        _db.ActivityLogs.Add(new ActivityLog
        {
            TenantId = Tenancy.TenantId,
            BranchId = Tenancy.BranchId,
            ActorUserId = _actor.UserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId is 0 ? null : entityId,
            Description = description,
            Metadata = JsonSerializer.Serialize(metadata),
        });
}
