using GymApis.Dtos;
using GymApis.Services;
using GymApis.Services.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GymApis.Controllers.Settings;

/// <summary>
/// Operational settings. Thin: read the request, delegate, map the result.
///
/// [Authorize] with ManageSettings sits on the CLASS, so a tab added later —
/// payments, retention, branding — is owner/admin before anyone remembers to
/// restrict it. That matters more here than on most controllers: everything
/// behind this route changes behaviour for the whole tenant rather than for one
/// record.
/// </summary>
[ApiController]
[Route("api/settings")]
[Authorize(Policy = AuthPolicies.ManageSettings)]
public class SettingsController : ControllerBase
{
    private readonly ISettingsService _service;

    public SettingsController(ISettingsService service) => _service = service;

    [HttpGet("messaging")]
    public async Task<IActionResult> Messaging(CancellationToken ct) =>
        Ok(await _service.GetMessagingAsync(ct));

    [HttpPut("messaging")]
    public async Task<IActionResult> UpdateMessaging(
        [FromBody] MessagingSettingsWriteRequest request, CancellationToken ct)
    {
        var result = await _service.UpdateMessagingAsync(request, ct);
        return result.Succeeded
            ? Ok(result.Value)
            : BadRequest(new { errors = new[] { result.Error } });
    }

    /// <summary>
    /// Recent outbound messages. Metadata only — see QueuedMessageResponse for
    /// why a body is never in the projection.
    /// </summary>
    [HttpGet("messaging/queue")]
    public async Task<IActionResult> Queue(
        [FromQuery] string? status,
        [FromQuery] string? purpose,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default) =>
        Ok(await _service.QueueAsync(status, purpose, page, pageSize, ct));
}
