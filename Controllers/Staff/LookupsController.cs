using GymApis.Dtos;
using GymApis.Repos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GymApis.Controllers.Staff;

public record ShiftOption(long Id, string Name, TimeOnly? StartTime, TimeOnly? EndTime);

/// <summary>
/// Dropdown data for the staff forms. Authenticated — the shift and tag names a
/// gym uses are not public information.
/// </summary>
[ApiController]
[Route("api/lookups")]
[Authorize]
public class LookupsController : ControllerBase
{
    private readonly GymDbContext _db;

    public LookupsController(GymDbContext db) => _db = db;

    [HttpGet("shifts")]
    public async Task<IActionResult> Shifts(CancellationToken ct) =>
        Ok(await _db.Shifts
            .Where(s => s.TenantId == Tenancy.TenantId && s.IsActive)
            .OrderBy(s => s.StartTime ?? TimeOnly.MaxValue)
            .Select(s => new ShiftOption(s.Id, s.Name, s.StartTime, s.EndTime))
            .ToListAsync(ct));

    [HttpGet("responsibilities")]
    public Task<IActionResult> Responsibilities(CancellationToken ct) => TagsIn("responsibility", ct);

    /// <summary>Options for the trainer form's Specialization select.</summary>
    [HttpGet("specializations")]
    public Task<IActionResult> Specializations(CancellationToken ct) => TagsIn("specialization", ct);

    /// <summary>
    /// Options for the membership plan form's "Included Services" grid. The
    /// catalog is data (`plan_services`), not a constant in the client, so
    /// adding a tenth service is an INSERT rather than a deploy.
    /// </summary>
    [HttpGet("plan-services")]
    public async Task<IActionResult> PlanServices(CancellationToken ct) =>
        Ok(await _db.PlanServices
            .Where(s => s.TenantId == Tenancy.TenantId && s.IsActive)
            .OrderBy(s => s.DisplayOrder).ThenBy(s => s.Name)
            .Select(s => new PlanServiceOption(s.Id, s.Name))
            .ToListAsync(ct));

    /// <summary>
    /// Options for the Add Member form's plan select. Duration and price ride
    /// along because the form auto-fills expiry date and membership price from
    /// the chosen plan — a second round trip per selection would be wasteful,
    /// and the numbers are re-read server-side anyway before anything is sold.
    /// </summary>
    [HttpGet("plans")]
    public async Task<IActionResult> Plans(CancellationToken ct) =>
        Ok(await _db.MembershipPlans
            .Where(p => p.TenantId == Tenancy.TenantId &&
                        (p.BranchId == null || p.BranchId == Tenancy.BranchId) &&
                        p.IsActive)
            .OrderBy(p => p.DisplayOrder).ThenBy(p => p.Name)
            .Select(p => new PlanOption(p.Id, p.Name, p.DurationValue, p.DurationUnit, p.Price))
            .ToListAsync(ct));

    private async Task<IActionResult> TagsIn(string category, CancellationToken ct) =>
        Ok(await _db.Tags
            .Where(t => t.TenantId == Tenancy.TenantId && t.Category == category && t.IsActive)
            .OrderBy(t => t.Name)
            .Select(t => new TagResponse(t.Id, t.Name))
            .ToListAsync(ct));
}
