using GymApis.Dtos;
using GymApis.Services;
using GymApis.Services.Staff;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GymApis.Controllers.Staff;

/// <summary>
/// CRUD shared by every staff role. Thin: validate-by-attribute, delegate, map
/// to a status code.
///
/// [Authorize(ManageStaff)] sits here rather than per action — a new endpoint
/// added by a subclass is protected by default rather than protected only if
/// someone remembers.
/// </summary>
[ApiController]
[Authorize(Policy = AuthPolicies.ManageStaff)]
public abstract class StaffControllerBase : ControllerBase
{
    private readonly IStaffService _service;

    protected StaffControllerBase(IStaffService service) => _service = service;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] StaffQuery query, CancellationToken ct)
        => Ok(await _service.ListAsync(query, ct));

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
    {
        var found = await _service.GetAsync(id, ct);
        return found is null ? NotFound() : Ok(found);
    }

    /// <summary>
    /// multipart/form-data so the photo rides along with the fields.
    /// RequestSizeLimit caps the whole request — the per-file 2 MB check in
    /// PhotoStorage runs after the body is accepted, so this is what stops a
    /// multi-gigabyte upload from ever being buffered.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(4 * 1024 * 1024)]
    // The IFormFile parameter would otherwise make multipart mandatory even for
    // a caller sending no photo at all.
    [Consumes("multipart/form-data", "application/x-www-form-urlencoded")]
    public async Task<IActionResult> Create(
        [FromForm] StaffWriteRequest request,
        IFormFile? photo,
        CancellationToken ct)
    {
        var result = await _service.CreateAsync(request, photo, ct);
        return result.Succeeded
            ? CreatedAtAction(nameof(Get), new { id = result.Value!.Id }, result.Value)
            : BadRequest(new { errors = new[] { result.Error } });
    }

    [HttpPut("{id:long}")]
    [RequestSizeLimit(4 * 1024 * 1024)]
    [Consumes("multipart/form-data", "application/x-www-form-urlencoded")]
    public async Task<IActionResult> Update(
        long id,
        [FromForm] StaffWriteRequest request,
        IFormFile? photo,
        [FromForm] bool removePhoto,
        CancellationToken ct)
    {
        var result = await _service.UpdateAsync(id, request, photo, removePhoto, ct);
        if (result.NotFound) return NotFound();
        return result.Succeeded
            ? Ok(result.Value)
            : BadRequest(new { errors = new[] { result.Error } });
    }

    /// <summary>Counts for the list's cards, over the whole branch for this role.</summary>
    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken ct) =>
        Ok(await _service.StatsAsync(ct));

    /// <summary>Soft delete — the row is hidden, never removed.</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        var result = await _service.DeleteAsync(id, ct);
        return result.NotFound ? NotFound() : NoContent();
    }

    /// <summary>
    /// DPDP erasure. Overwrites personal data and destroys the photo while the
    /// employment trail survives. Irreversible — there is no copy of what was
    /// overwritten, which is the entire point.
    /// </summary>
    [HttpPost("{id:long}/erase")]
    [Authorize(Policy = AuthPolicies.EraseStaff)]
    public async Task<IActionResult> Erase(long id, CancellationToken ct)
    {
        var result = await _service.EraseAsync(id, ct);
        return result.NotFound ? NotFound() : NoContent();
    }
}

[Route("api/receptionists")]
public class ReceptionistsController : StaffControllerBase
{
    public ReceptionistsController(IReceptionistService service) : base(service) { }
}

[Route("api/trainers")]
public class TrainersController : StaffControllerBase
{
    public TrainersController(ITrainerService service) : base(service) { }
}
