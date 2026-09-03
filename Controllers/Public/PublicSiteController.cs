using GymApis.Dtos;
using GymApis.Services;
using GymApis.Services.Site;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GymApis.Controllers.Public;

/// <summary>
/// The marketing website's read API. The only anonymous surface in this
/// application besides sign-in.
///
/// [AllowAnonymous] is MANDATORY here and not a stylistic choice: Program.cs
/// sets a FallbackPolicy requiring an authenticated user, so without this
/// attribute every endpoint below answers 401 to the visitors it exists for.
/// That default is fail-closed and worth keeping — this is the one controller
/// that opts out of it, which is why it is its own class in its own folder
/// rather than a few actions bolted onto the CMS controller.
///
/// [EnableRateLimiting] is the other half. An endpoint anyone can call without
/// credentials is an endpoint anyone can call in a loop, and every response
/// here costs several database round trips.
///
/// The service handed in is IPublicSiteService, which has no write verbs on it
/// at all — the visibility and consent filters cannot be bypassed from here
/// because the methods that would bypass them are not in scope.
/// </summary>
[ApiController]
[Route("api/public")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.Public)]
public class PublicSiteController : ControllerBase
{
    private readonly IPublicSiteService _site;

    public PublicSiteController(IPublicSiteService site) => _site = site;

    /// <summary>
    /// Everything the site needs to paint, in one response — the same
    /// one-endpoint-one-instant rule the dashboard follows. The two unbounded
    /// lists ship a first page here and have their own endpoints below.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(await _site.GetAsync(ct));

    [HttpGet("gallery")]
    public async Task<IActionResult> Gallery([FromQuery] PublicPageQuery query, CancellationToken ct) =>
        Ok(await _site.GalleryAsync(query, ct));

    [HttpGet("transformations")]
    public async Task<IActionResult> Transformations(
        [FromQuery] PublicPageQuery query, CancellationToken ct) =>
        Ok(await _site.TransformationsAsync(query, ct));

    [HttpGet("events")]
    public async Task<IActionResult> Events([FromQuery] PublicPageQuery query, CancellationToken ct) =>
        Ok(await _site.EventsAsync(query, ct));
}
