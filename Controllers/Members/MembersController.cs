using GymApis.Dtos;
using GymApis.Services;
using GymApis.Services.Members;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GymApis.Controllers.Members;

/// <summary>
/// Members CRUD. Thin by design: read the request, delegate, map the result to
/// a status code.
///
/// [Authorize(ManageMembers)] sits on the class, not on each action, so an
/// endpoint added later is protected before anyone remembers to protect it.
/// The two destructive actions raise the bar to EraseMembers.
/// </summary>
[ApiController]
[Route("api/members")]
[Authorize(Policy = AuthPolicies.ManageMembers)]
public class MembersController : ControllerBase
{
    private readonly IMemberService _service;

    public MembersController(IMemberService service) => _service = service;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] MemberQuery query, CancellationToken ct) =>
        Ok(await _service.ListAsync(query, ct));

    /// <summary>The five cards above the members table.</summary>
    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken ct) =>
        Ok(await _service.StatsAsync(ct));

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
        [FromForm] MemberWriteRequest request,
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
        [FromForm] MemberWriteRequest request,
        IFormFile? photo,
        [FromForm] bool removePhoto,
        CancellationToken ct)
    {
        var result = await _service.UpdateAsync(id, request, photo, removePhoto, ct);

        // Order matters: Missing() also has Succeeded == false, so a missing
        // member must be checked first or it answers 400 instead of 404.
        if (result.NotFound) return NotFound();

        return result.Succeeded
            ? Ok(result.Value)
            : BadRequest(new { errors = new[] { result.Error } });
    }

    /// <summary>Sells a new membership: the "Renew / New" button.</summary>
    [HttpPost("{id:long}/memberships")]
    public async Task<IActionResult> Renew(
        long id, [FromBody] MembershipWriteRequest request, CancellationToken ct)
    {
        var result = await _service.RenewAsync(id, request, ct);

        if (result.NotFound) return NotFound();

        return result.Succeeded
            ? Ok(result.Value)
            : BadRequest(new { errors = new[] { result.Error } });
    }

    /// <summary>
    /// Edits an existing membership — a plan change, an extension, a corrected
    /// expiry or discount. Renewing is a different verb: it writes a new dated
    /// row, this one rewrites the agreement that is already in force.
    /// </summary>
    [HttpPut("{id:long}/memberships/{membershipId:long}")]
    public async Task<IActionResult> UpdateMembership(
        long id, long membershipId, [FromBody] MembershipUpdateRequest request, CancellationToken ct)
    {
        var result = await _service.UpdateMembershipAsync(id, membershipId, request, ct);

        if (result.NotFound) return NotFound();

        return result.Succeeded
            ? Ok(result.Value)
            : BadRequest(new { errors = new[] { result.Error } });
    }

    /// <summary>
    /// Records one payment against an existing membership. Instalments are
    /// several of these; the balance is summed from them, never stored.
    /// </summary>
    [HttpPost("{id:long}/memberships/{membershipId:long}/payments")]
    public async Task<IActionResult> AddPayment(
        long id, long membershipId, [FromBody] PaymentWriteRequest request, CancellationToken ct)
    {
        var result = await _service.AddPaymentAsync(id, membershipId, request, ct);

        if (result.NotFound) return NotFound();

        return result.Succeeded
            ? Ok(result.Value)
            : BadRequest(new { errors = new[] { result.Error } });
    }

    /// <summary>
    /// Activate / deactivate. Not a DELETE: nothing is removed and nothing is
    /// hidden — a deactivated member stays on the list under the Inactive
    /// filter, and the switch flips back.
    ///
    /// Left at the controller's ManageMembers policy rather than the stricter
    /// EraseMembers the old DELETE carried. PUT /api/members/{id} already binds
    /// and writes Status under ManageMembers, so a receptionist could set a
    /// member inactive through the form regardless; a second, harder gate on
    /// the same field would be theatre.
    /// </summary>
    [HttpPatch("{id:long}/status")]
    public async Task<IActionResult> SetStatus(
        long id, [FromBody] MemberStatusRequest req, CancellationToken ct)
    {
        var result = await _service.SetStatusAsync(id, req.Status, ct);
        return result.NotFound ? NotFound() : NoContent();
    }

    /// <summary>
    /// DPDP erasure. Overwrites personal data and destroys the photo while the
    /// payments ledger survives. Irreversible — there is no copy of what was
    /// overwritten, which is the entire point.
    /// </summary>
    [HttpPost("{id:long}/erase")]
    [Authorize(Policy = AuthPolicies.EraseMembers)]
    public async Task<IActionResult> Erase(long id, CancellationToken ct)
    {
        var result = await _service.EraseAsync(id, ct);
        return result.NotFound ? NotFound() : NoContent();
    }
}
