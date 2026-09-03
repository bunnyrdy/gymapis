using GymApis.Dtos;
using GymApis.Services.Staff;   // ServiceResult<T> — generic, shared across modules

namespace GymApis.Services.Site;

/// <summary>
/// The CMS behind the public website. Everything here is admin-side and sits
/// behind <see cref="AuthPolicies.ManageSite"/>; the anonymous read model is
/// <see cref="IPublicSiteService"/>.
///
/// The five content types share one interface rather than five, because they
/// share one screen group and one set of verbs. Where they differ — a
/// transformation's consent, an event's date — the difference is in the DTO,
/// not in the shape of the call.
/// </summary>
public interface ISiteService
{
    // Settings (a singleton — no id, no create, no delete)
    Task<SiteSettingsResponse> GetSettingsAsync(CancellationToken ct);
    Task<ServiceResult<SiteSettingsResponse>> UpdateSettingsAsync(
        SiteSettingsWriteRequest req, CancellationToken ct);

    /// <summary>
    /// Replaces one piece of site artwork. <paramref name="slot"/> is a closed
    /// set (hero, heroVideo, logo, login) resolved inside the service — the
    /// caller never names a column or a path.
    /// </summary>
    Task<ServiceResult<SiteSettingsResponse>> SetArtworkAsync(
        string slot, IFormFile? file, bool remove, CancellationToken ct);

    // Media
    Task<PagedResult<SiteMediaResponse>> ListMediaAsync(SiteContentQuery q, CancellationToken ct);
    Task<ServiceResult<SiteMediaResponse>> AddMediaAsync(
        SiteMediaWriteRequest req, IFormFile? file, IFormFile? poster, CancellationToken ct);
    Task<ServiceResult<SiteMediaResponse>> UpdateMediaAsync(
        long id, SiteMediaWriteRequest req, CancellationToken ct);
    Task<ServiceResult<bool>> DeleteMediaAsync(long id, CancellationToken ct);

    // Offers
    Task<PagedResult<SiteOfferResponse>> ListOffersAsync(SiteContentQuery q, CancellationToken ct);
    Task<ServiceResult<SiteOfferResponse>> SaveOfferAsync(
        long? id, SiteOfferWriteRequest req, CancellationToken ct);
    Task<ServiceResult<bool>> DeleteOfferAsync(long id, CancellationToken ct);

    // Transformations
    Task<PagedResult<SiteTransformationResponse>> ListTransformationsAsync(
        SiteContentQuery q, CancellationToken ct);
    Task<ServiceResult<SiteTransformationResponse>> SaveTransformationAsync(
        long? id, SiteTransformationWriteRequest req,
        IFormFile? before, IFormFile? after, CancellationToken ct);
    Task<ServiceResult<bool>> DeleteTransformationAsync(long id, CancellationToken ct);

    // Events
    Task<PagedResult<SiteEventResponse>> ListEventsAsync(SiteContentQuery q, CancellationToken ct);
    Task<ServiceResult<SiteEventResponse>> SaveEventAsync(
        long? id, SiteEventWriteRequest req, IFormFile? image, CancellationToken ct);
    Task<ServiceResult<bool>> DeleteEventAsync(long id, CancellationToken ct);

    /// <summary>
    /// One reorder for all four content types. <paramref name="kind"/> is a
    /// closed set resolved inside the service.
    /// </summary>
    Task<ServiceResult<bool>> ReorderAsync(string kind, long[] orderedIds, CancellationToken ct);
}

/// <summary>
/// The anonymous read model. Split from <see cref="ISiteService"/> so the
/// public controller is handed a dependency that has no write verbs on it at
/// all — the visibility and consent filters cannot be bypassed by reaching for
/// the wrong method, because the wrong method is not in scope.
/// </summary>
public interface IPublicSiteService
{
    Task<PublicSiteResponse> GetAsync(CancellationToken ct);
    Task<PagedResult<PublicMediaItem>> GalleryAsync(PublicPageQuery q, CancellationToken ct);
    Task<PagedResult<PublicTransformation>> TransformationsAsync(PublicPageQuery q, CancellationToken ct);
    Task<PagedResult<PublicEvent>> EventsAsync(PublicPageQuery q, CancellationToken ct);
}
