using GymApis.Dtos;
using GymApis.Services;
using GymApis.Services.Site;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GymApis.Controllers.Site;

/// <summary>
/// The website CMS. Thin by design: read the request, delegate, map the result
/// to a status code.
///
/// [Authorize] sits on the class, not on each action, so an endpoint added
/// later is protected before anyone remembers to protect it. The anonymous
/// half of this module is <see cref="Public.PublicSiteController"/>, and the
/// two are separate classes precisely so no one has to remember which action on
/// a shared controller was meant to be open.
///
/// Every upload action carries its own [RequestSizeLimit]: images are capped at
/// 4 MB and the one video endpoint at 34 MB, so a video ceiling is never
/// available to a photo endpoint. PayloadSizeGuard turns the overflow into a
/// 413 before model binding reads a byte.
/// </summary>
[ApiController]
[Route("api/site")]
[Authorize(Policy = AuthPolicies.ManageSite)]
public class SiteController : ControllerBase
{
    private const int PhotoLimit = 4 * 1024 * 1024;
    private const int VideoLimit = 34 * 1024 * 1024;
    // Both, matching StaffControllerBase: an edit that changes no artwork is a
    // plain urlencoded form, and refusing it would 415 every metadata-only save.
    private const string Multipart = "multipart/form-data";
    private const string UrlEncoded = "application/x-www-form-urlencoded";

    private readonly ISiteService _service;

    public SiteController(ISiteService service) => _service = service;

    // -----------------------------------------------------------------------
    // SETTINGS
    // -----------------------------------------------------------------------

    [HttpGet("settings")]
    public async Task<IActionResult> Settings(CancellationToken ct) =>
        Ok(await _service.GetSettingsAsync(ct));

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] SiteSettingsWriteRequest request, CancellationToken ct)
    {
        var result = await _service.UpdateSettingsAsync(request, ct);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { errors = new[] { result.Error } });
    }

    /// <summary>
    /// Replaces the hero image, website logo or login artwork. The slot is a
    /// route value matched against a closed set inside the service — it never
    /// reaches a column name or a path.
    /// </summary>
    [HttpPost("settings/artwork/{slot}")]
    [RequestSizeLimit(PhotoLimit)]
    [Consumes(Multipart, UrlEncoded)]
    public async Task<IActionResult> SetArtwork(
        string slot, IFormFile? file, [FromForm] bool remove, CancellationToken ct)
    {
        if (slot == "heroVideo")
            return BadRequest(new { errors = new[] { "Use the hero video endpoint for videos." } });

        var result = await _service.SetArtworkAsync(slot, file, remove, ct);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { errors = new[] { result.Error } });
    }

    /// <summary>
    /// The hero video, separated from the other artwork slots for one reason:
    /// its size ceiling. Sharing an action would mean the 34 MB limit applied
    /// to the logo upload too.
    /// </summary>
    [HttpPost("settings/artwork/hero-video")]
    [RequestSizeLimit(VideoLimit)]
    [Consumes(Multipart, UrlEncoded)]
    public async Task<IActionResult> SetHeroVideo(
        IFormFile? file, [FromForm] bool remove, CancellationToken ct)
    {
        var result = await _service.SetArtworkAsync("heroVideo", file, remove, ct);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { errors = new[] { result.Error } });
    }

    // -----------------------------------------------------------------------
    // MEDIA — the About collage and the Gallery
    // -----------------------------------------------------------------------

    [HttpGet("media")]
    public async Task<IActionResult> Media([FromQuery] SiteContentQuery query, CancellationToken ct) =>
        Ok(await _service.ListMediaAsync(query, ct));

    /// <summary>
    /// Accepts an image or a video on one endpoint, so the CMS has one drop
    /// target. The video ceiling applies because either may arrive; the service
    /// still validates each kind against its own magic-byte table.
    /// </summary>
    [HttpPost("media")]
    [RequestSizeLimit(VideoLimit)]
    [Consumes(Multipart, UrlEncoded)]
    public async Task<IActionResult> AddMedia(
        [FromForm] SiteMediaWriteRequest request,
        IFormFile? file,
        IFormFile? poster,
        CancellationToken ct)
    {
        var result = await _service.AddMediaAsync(request, file, poster, ct);
        return result.Succeeded
            ? Ok(result.Value)
            : BadRequest(new { errors = new[] { result.Error } });
    }

    [HttpPut("media/{id:long}")]
    public async Task<IActionResult> UpdateMedia(
        long id, [FromBody] SiteMediaWriteRequest request, CancellationToken ct)
    {
        var result = await _service.UpdateMediaAsync(id, request, ct);

        // Order matters: Missing() also has Succeeded == false, so a missing
        // row must be checked first or it answers 400 instead of 404.
        if (result.NotFound) return NotFound();
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { errors = new[] { result.Error } });
    }

    [HttpDelete("media/{id:long}")]
    public async Task<IActionResult> DeleteMedia(long id, CancellationToken ct)
    {
        var result = await _service.DeleteMediaAsync(id, ct);
        return result.NotFound ? NotFound() : NoContent();
    }

    // -----------------------------------------------------------------------
    // OFFERS
    // -----------------------------------------------------------------------

    [HttpGet("offers")]
    public async Task<IActionResult> Offers([FromQuery] SiteContentQuery query, CancellationToken ct) =>
        Ok(await _service.ListOffersAsync(query, ct));

    [HttpPost("offers")]
    public async Task<IActionResult> CreateOffer(
        [FromBody] SiteOfferWriteRequest request, CancellationToken ct)
    {
        var result = await _service.SaveOfferAsync(null, request, ct);
        return result.Succeeded
            ? CreatedAtAction(nameof(Offers), new { }, result.Value)
            : BadRequest(new { errors = new[] { result.Error } });
    }

    [HttpPut("offers/{id:long}")]
    public async Task<IActionResult> UpdateOffer(
        long id, [FromBody] SiteOfferWriteRequest request, CancellationToken ct)
    {
        var result = await _service.SaveOfferAsync(id, request, ct);
        if (result.NotFound) return NotFound();
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { errors = new[] { result.Error } });
    }

    [HttpDelete("offers/{id:long}")]
    public async Task<IActionResult> DeleteOffer(long id, CancellationToken ct)
    {
        var result = await _service.DeleteOfferAsync(id, ct);
        return result.NotFound ? NotFound() : NoContent();
    }

    // -----------------------------------------------------------------------
    // TRANSFORMATIONS
    // -----------------------------------------------------------------------

    [HttpGet("transformations")]
    public async Task<IActionResult> Transformations(
        [FromQuery] SiteContentQuery query, CancellationToken ct) =>
        Ok(await _service.ListTransformationsAsync(query, ct));

    [HttpPost("transformations")]
    [RequestSizeLimit(PhotoLimit * 2)]   // two photos on one request
    [Consumes(Multipart, UrlEncoded)]
    public async Task<IActionResult> CreateTransformation(
        [FromForm] SiteTransformationWriteRequest request,
        IFormFile? before,
        IFormFile? after,
        CancellationToken ct)
    {
        var result = await _service.SaveTransformationAsync(null, request, before, after, ct);
        return result.Succeeded
            ? CreatedAtAction(nameof(Transformations), new { }, result.Value)
            : BadRequest(new { errors = new[] { result.Error } });
    }

    [HttpPut("transformations/{id:long}")]
    [RequestSizeLimit(PhotoLimit * 2)]
    [Consumes(Multipart, UrlEncoded)]
    public async Task<IActionResult> UpdateTransformation(
        long id,
        [FromForm] SiteTransformationWriteRequest request,
        IFormFile? before,
        IFormFile? after,
        CancellationToken ct)
    {
        var result = await _service.SaveTransformationAsync(id, request, before, after, ct);
        if (result.NotFound) return NotFound();
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { errors = new[] { result.Error } });
    }

    [HttpDelete("transformations/{id:long}")]
    public async Task<IActionResult> DeleteTransformation(long id, CancellationToken ct)
    {
        var result = await _service.DeleteTransformationAsync(id, ct);
        return result.NotFound ? NotFound() : NoContent();
    }

    // -----------------------------------------------------------------------
    // EVENTS
    // -----------------------------------------------------------------------

    [HttpGet("events")]
    public async Task<IActionResult> Events([FromQuery] SiteContentQuery query, CancellationToken ct) =>
        Ok(await _service.ListEventsAsync(query, ct));

    [HttpPost("events")]
    [RequestSizeLimit(PhotoLimit)]
    [Consumes(Multipart, UrlEncoded)]
    public async Task<IActionResult> CreateEvent(
        [FromForm] SiteEventWriteRequest request, IFormFile? image, CancellationToken ct)
    {
        var result = await _service.SaveEventAsync(null, request, image, ct);
        return result.Succeeded
            ? CreatedAtAction(nameof(Events), new { }, result.Value)
            : BadRequest(new { errors = new[] { result.Error } });
    }

    [HttpPut("events/{id:long}")]
    [RequestSizeLimit(PhotoLimit)]
    [Consumes(Multipart, UrlEncoded)]
    public async Task<IActionResult> UpdateEvent(
        long id, [FromForm] SiteEventWriteRequest request, IFormFile? image, CancellationToken ct)
    {
        var result = await _service.SaveEventAsync(id, request, image, ct);
        if (result.NotFound) return NotFound();
        return result.Succeeded ? Ok(result.Value) : BadRequest(new { errors = new[] { result.Error } });
    }

    [HttpDelete("events/{id:long}")]
    public async Task<IActionResult> DeleteEvent(long id, CancellationToken ct)
    {
        var result = await _service.DeleteEventAsync(id, ct);
        return result.NotFound ? NotFound() : NoContent();
    }

    // -----------------------------------------------------------------------
    // REORDER
    // -----------------------------------------------------------------------

    /// <summary>
    /// One endpoint for all four content types. The body carries the whole
    /// ordered run rather than a swap, which makes the write idempotent.
    /// </summary>
    [HttpPatch("{kind}/reorder")]
    public async Task<IActionResult> Reorder(
        string kind, [FromBody] ReorderRequest request, CancellationToken ct)
    {
        var result = await _service.ReorderAsync(kind, request.OrderedIds, ct);
        return result.Succeeded ? NoContent() : BadRequest(new { errors = new[] { result.Error } });
    }
}
