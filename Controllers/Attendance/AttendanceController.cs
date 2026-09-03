using GymApis.Dtos;
using GymApis.Services;
using GymApis.Services.Attendance;
using GymApis.Services.Staff;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GymApis.Controllers.Attendance;

/// <summary>
/// The shared staff attendance module. Thin by design: read the request,
/// delegate, map the result to a status code.
///
/// Reading the roster is open to any signed-in user — a trainer checking their
/// own record is normal — but the two writes are not. Marking is
/// [MarkAttendance], which is the managerial roles plus receptionist, because
/// taking the register is exactly what the front desk is there for. Rewriting a
/// record that already exists is [ManageStaff]: a correction changes what the
/// employment log claims about someone's attendance, and that is not a
/// front-desk decision.
/// </summary>
[ApiController]
[Route("api/attendance")]
[Authorize]
public class AttendanceController : ControllerBase
{
    private readonly IAttendanceService _service;

    public AttendanceController(IAttendanceService service) => _service = service;

    /// <summary>The Shift Roaster for one day. Defaults to today at the branch.</summary>
    [HttpGet]
    public async Task<IActionResult> Roster([FromQuery] AttendanceQuery query, CancellationToken ct) =>
        Ok(await _service.RosterAsync(query, ct));

    /// <summary>The four cards above the roster.</summary>
    [HttpGet("stats")]
    public async Task<IActionResult> Stats(
        [FromQuery] DateOnly? date, [FromQuery] string? role, CancellationToken ct) =>
        Ok(await _service.StatsAsync(date, role, ct));

    /// <summary>Profile, monthly totals and the calendar grid for one person.</summary>
    [HttpGet("staff/{staffId:long}")]
    public async Task<IActionResult> Detail(
        long staffId, [FromQuery] DateOnly? month, CancellationToken ct)
    {
        var found = await _service.DetailAsync(staffId, month, ct);
        return found is null ? NotFound() : Ok(found);
    }

    /// <summary>The paginated Attendance Log beneath the calendar.</summary>
    [HttpGet("staff/{staffId:long}/log")]
    public async Task<IActionResult> Log(
        long staffId, [FromQuery] AttendanceLogQuery query, CancellationToken ct) =>
        Respond(await _service.LogAsync(staffId, query, ct));

    /// <summary>
    /// Records a mark for a day that has none.
    ///
    /// Back-dating is checked inside the service against the branch's own
    /// calendar, not this one's, so the privilege is read here and passed in
    /// rather than the service reaching for HttpContext.
    /// </summary>
    [HttpPost("mark")]
    [Authorize(Policy = AuthPolicies.MarkAttendance)]
    public async Task<IActionResult> Mark([FromBody] MarkAttendanceRequest request, CancellationToken ct)
    {
        var canBackdate = AuthPolicies.ManageStaffRoles.Any(User.IsInRole);
        return Respond(await _service.MarkAsync(request, canBackdate, ct));
    }

    /// <summary>
    /// Rewrites an existing record — the "authorized users may later correct
    /// attendance" rule. Always managerial, whatever the date.
    /// </summary>
    [HttpPut("{id:long}")]
    [Authorize(Policy = AuthPolicies.ManageStaff)]
    public async Task<IActionResult> Correct(
        long id, [FromBody] CorrectAttendanceRequest request, CancellationToken ct) =>
        Respond(await _service.CorrectAsync(id, request, ct));

    /// <summary>
    /// One mapping from envelope to status code, so every write on this
    /// controller answers the same way. Order matters: Missing, Denied and
    /// Clash all have Succeeded == false, so each must be tested before the
    /// generic 400 or a 404 comes back as a bad request.
    /// </summary>
    private IActionResult Respond<T>(ServiceResult<T> result)
    {
        if (result.NotFound) return NotFound();
        if (result.Forbidden) return Forbid();
        if (result.Conflict) return Conflict(new { errors = new[] { result.Error } });

        return result.Succeeded
            ? Ok(result.Value)
            : BadRequest(new { errors = new[] { result.Error } });
    }
}
