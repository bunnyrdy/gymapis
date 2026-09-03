using GymApis.Dtos;
using GymApis.Services.Dashboard;
using Microsoft.AspNetCore.Mvc;

namespace GymApis.Controllers.Dashboard;

/// <summary>
/// The landing screen. One GET, one document.
///
/// No [Authorize] attribute and no policy: the fallback policy in Program.cs
/// already requires a valid token, and the dashboard is deliberately open to
/// every signed-in role — the expiring list and today's register are the front
/// desk's own work queue. The one restricted part of the response, the money
/// totals, is enforced inside DashboardService, because there the unit of
/// authorization is two fields rather than the endpoint.
/// </summary>
[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly IDashboardService _service;

    public DashboardController(IDashboardService service) => _service = service;

    /// <summary>Every card, list and chart on the dashboard, as one payload.</summary>
    [HttpGet]
    public async Task<ActionResult<DashboardResponse>> Get(CancellationToken ct) =>
        Ok(await _service.SummaryAsync(ct));
}
