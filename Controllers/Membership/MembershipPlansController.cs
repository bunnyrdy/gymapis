using GymApis.Dtos;
using GymApis.Services;
using GymApis.Services.Membership;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GymApis.Controllers.Membership;

/// <summary>
/// Membership plans CRUD. Thin by design: read the request, delegate, map the
/// result to a status code.
///
/// [Authorize] sits on the class, not on each action, so an endpoint added
/// later is protected before anyone remembers to protect it.
/// </summary>
[ApiController]
[Route("api/membership-plans")]
[Authorize(Policy = AuthPolicies.ManagePlans)]
public class MembershipPlansController : ControllerBase
{
    private readonly IMembershipPlanService _service;

    public MembershipPlansController(IMembershipPlanService service) => _service = service;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] MembershipPlanQuery query, CancellationToken ct) =>
        Ok(await _service.ListAsync(query, ct));

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
    {
        var found = await _service.GetAsync(id, ct);
        return found is null ? NotFound() : Ok(found);
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] MembershipPlanWriteRequest request, CancellationToken ct)
    {
        var result = await _service.CreateAsync(request, ct);

        return result.Succeeded
            ? CreatedAtAction(nameof(Get), new { id = result.Value!.Id }, result.Value)
            : BadRequest(new { errors = new[] { result.Error } });
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(
        long id, [FromBody] MembershipPlanWriteRequest request, CancellationToken ct)
    {
        var result = await _service.UpdateAsync(id, request, ct);

        // Order matters: Missing() also has Succeeded == false, so a missing
        // plan must be checked first or it answers 400 instead of 404.
        if (result.NotFound) return NotFound();

        return result.Succeeded
            ? Ok(result.Value)
            : BadRequest(new { errors = new[] { result.Error } });
    }

    /// <summary>Archives the plan. Soft only — `memberships` rows point at it.</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Archive(long id, CancellationToken ct)
    {
        var result = await _service.ArchiveAsync(id, ct);
        return result.NotFound ? NotFound() : NoContent();
    }
}
