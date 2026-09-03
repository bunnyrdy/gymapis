using System.Text.Json;
using GymApis.Dtos;
using GymApis.Models.Common;
using GymApis.Models.Membership;
using GymApis.Repos;
using GymApis.Services.Staff;   // ServiceResult<T>
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GymApis.Services.Membership;

/// <summary>
/// Membership plans: what the gym sells. Follows the same shape as
/// StaffServiceBase — one scoping helper, a DTO allow-list, plan-code
/// generation with a retry, auditing on every mutation and soft delete.
///
/// Two differences worth remembering:
///   * soft delete is `archived_at`, not `deleted_at`;
///   * `branch_id` is nullable, and NULL means "sold at every branch". V1
///     writes NULL and reads both, so a per-branch plan created later still
///     shows up at the branch that owns it.
/// </summary>
public class MembershipPlanService : IMembershipPlanService
{
    private const string CodePrefix = "PLN-";

    private readonly GymDbContext _db;
    private readonly ICurrentUser _actor;
    private readonly ILogger<MembershipPlanService> _log;

    public MembershipPlanService(GymDbContext db, ICurrentUser actor, ILogger<MembershipPlanService> log)
    {
        _db = db;
        _actor = actor;
        _log = log;
    }

    // -----------------------------------------------------------------------
    // SCOPE
    // -----------------------------------------------------------------------
    /// <summary>
    /// The only place tenant + branch filtering is expressed. Every read and
    /// write path starts here, so no query can silently omit it — which also
    /// makes it the IDOR guard: another tenant's plan id simply does not exist.
    ///
    /// Archived rows are excluded by the global query filter on the entity.
    /// </summary>
    private IQueryable<MembershipPlan> Scoped() =>
        _db.MembershipPlans.Where(p =>
            p.TenantId == Tenancy.TenantId &&
            (p.BranchId == null || p.BranchId == Tenancy.BranchId));

    // -----------------------------------------------------------------------
    // LIST
    // -----------------------------------------------------------------------
    public async Task<PagedResult<MembershipPlanListItem>> ListAsync(
        MembershipPlanQuery q, CancellationToken ct)
    {
        var rows = Scoped();

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = $"%{q.Search.Trim()}%";
            rows = rows.Where(p =>
                EF.Functions.ILike(p.Name, term) ||
                (p.PlanCode != null && EF.Functions.ILike(p.PlanCode, term)));
        }

        // Unrecognised values are ignored rather than erroring — a stale
        // bookmark with ?status=foo should show the list, not a 400.
        if (q.Status is "active") rows = rows.Where(p => p.IsActive);
        else if (q.Status is "inactive") rows = rows.Where(p => !p.IsActive);

        var total = await rows.CountAsync(ct);

        var items = await rows
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .Select(p => new MembershipPlanListItem(
                p.Id,
                p.Name,
                p.PlanCode,
                p.DurationValue,
                p.DurationUnit,
                p.Price,
                p.IsActive,
                p.IsFeatured,
                p.DisplayOrder,
                p.PlanServices.Count))
            .ToListAsync(ct);

        return new PagedResult<MembershipPlanListItem>(items, q.Page, q.PageSize, total);
    }

    // -----------------------------------------------------------------------
    // GET
    // -----------------------------------------------------------------------
    public async Task<MembershipPlanResponse?> GetAsync(long id, CancellationToken ct)
    {
        var plan = await Scoped()
            .Include(p => p.PlanServices).ThenInclude(s => s.Service)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        return plan is null ? null : ToResponse(plan);
    }

    // -----------------------------------------------------------------------
    // CREATE
    // -----------------------------------------------------------------------
    public async Task<ServiceResult<MembershipPlanResponse>> CreateAsync(
        MembershipPlanWriteRequest req, CancellationToken ct)
    {
         if(!await IsNameUniqueAsync(req.Name, null, ct))
            return ServiceResult<MembershipPlanResponse>.Fail("A plan with that name already exists.");
        var serviceIds = await ValidServiceIdsAsync(req.ServiceIds, ct);
        if (serviceIds is null)
            return ServiceResult<MembershipPlanResponse>.Fail("One or more services are not available.");

        var plan = new MembershipPlan
        {
            TenantId = Tenancy.TenantId,
            BranchId = null,        // sold at every branch — the normal case
        };
        Apply(req, plan);

        var supplied = Clean(req.PlanCode);
        if (supplied is not null)
        {
            plan.PlanCode = supplied;
            _db.MembershipPlans.Add(plan);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // A caller-chosen code that collides is bad input, not a bug —
                // 400 with a message beats a 500 with a Postgres error string.
                return ServiceResult<MembershipPlanResponse>.Fail("That plan code is already in use.");
            }
        }
        else
        {
            // Generated codes race: two concurrent creates can pick the same
            // number. Retry on the unique violation rather than locking.
            for (var attempt = 1; ; attempt++)
            {
                plan.PlanCode = await NextPlanCodeAsync(ct);
                _db.MembershipPlans.Add(plan);
                try
                {
                    await _db.SaveChangesAsync(ct);
                    break;
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex) && attempt < 5)
                {
                    _db.Entry(plan).State = EntityState.Detached;
                    _log.LogInformation("Plan code {Code} taken, retrying (attempt {Attempt}).",
                        plan.PlanCode, attempt);
                }
            }
        }

        await ReplaceServicesAsync(plan.Id, serviceIds, ct);
        await AuditAsync("plan.created", plan, $"Created plan {plan.Name}.", ct);
        await _db.SaveChangesAsync(ct);

        return ServiceResult<MembershipPlanResponse>.Ok((await GetAsync(plan.Id, ct))!);
    }

    // -----------------------------------------------------------------------
    // UPDATE
    // -----------------------------------------------------------------------
    public async Task<ServiceResult<MembershipPlanResponse>> UpdateAsync(
        long id, MembershipPlanWriteRequest req, CancellationToken ct)
    {
        var plan = await Scoped()
            .Include(p => p.PlanServices)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (plan is null) return ServiceResult<MembershipPlanResponse>.Missing();

        if (!await IsNameUniqueAsync(req.Name, id, ct))
            return ServiceResult<MembershipPlanResponse>.Fail("A plan with that name already exists.");

        var serviceIds = await ValidServiceIdsAsync(req.ServiceIds, ct);
        if (serviceIds is null)
            return ServiceResult<MembershipPlanResponse>.Fail("One or more services are not available.");

        Apply(req, plan);

        // PlanCode stays server-owned once assigned unless the caller sends a
        // new one; blanking the field must not wipe an existing code.
        var supplied = Clean(req.PlanCode);
        if (supplied is not null) plan.PlanCode = supplied;

        await ReplaceServicesAsync(plan.Id, serviceIds, ct);
        await AuditAsync("plan.updated", plan, $"Updated plan {plan.Name}.", ct);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return ServiceResult<MembershipPlanResponse>.Fail("That plan code is already in use.");
        }

        return ServiceResult<MembershipPlanResponse>.Ok((await GetAsync(plan.Id, ct))!);
    }

    // -----------------------------------------------------------------------
    // ARCHIVE (soft delete)
    // -----------------------------------------------------------------------
    /// <summary>
    /// Never a hard delete: `memberships` rows point at the plan a member
    /// bought, and deleting it would orphan their history. Archiving also
    /// deactivates, so an archived plan cannot be sold if it is ever restored
    /// by hand without a second look.
    /// </summary>
    public async Task<ServiceResult<bool>> ArchiveAsync(long id, CancellationToken ct)
    {
        var plan = await Scoped().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return ServiceResult<bool>.Missing();

        plan.ArchivedAt = DateTimeOffset.UtcNow;
        plan.IsActive = false;

        await AuditAsync("plan.archived", plan, $"Archived plan {plan.Name}.", ct);
        await _db.SaveChangesAsync(ct);

        return ServiceResult<bool>.Ok(true);
    }

    // -----------------------------------------------------------------------
    // INTERNALS
    // -----------------------------------------------------------------------

    /// <summary>Copies only DTO fields onto the entity. Ids and tenancy stay server-owned.</summary>
    private static void Apply(MembershipPlanWriteRequest req, MembershipPlan plan)
    {
        plan.Name = req.Name.Trim();
        plan.Description = Clean(req.Description);
        plan.DurationValue = req.DurationValue;
        plan.DurationUnit = req.DurationUnit;
        plan.Price = req.Price;
        plan.IsActive = req.IsActive;
        plan.IsFeatured = req.IsFeatured;
        plan.DisplayOrder = req.DisplayOrder;
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>Next code in the PLN-0001 series. Racy by nature — the caller retries.</summary>
    private async Task<string> NextPlanCodeAsync(CancellationToken ct)
    {
        var codes = await _db.MembershipPlans
            .IgnoreQueryFilters()      // an archived plan still owns its code
            .Where(p => p.TenantId == Tenancy.TenantId &&
                        p.PlanCode != null && p.PlanCode.StartsWith(CodePrefix))
            .Select(p => p.PlanCode!)
            .ToListAsync(ct);

        var highest = codes
            .Select(c => int.TryParse(c[CodePrefix.Length..], out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();

        return $"{CodePrefix}{highest + 1:D4}";
    }

    private async Task<bool> IsNameUniqueAsync(string name, long? ignoreId, CancellationToken ct)
    {
        var query  = Scoped().Where(p => p.Name == name.Trim());
        if (ignoreId is not null) query = query.Where(p => p.Id != ignoreId.Value);
        return !await query.AnyAsync(ct);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// Returns null when any id is unknown, retired or belongs to another
    /// tenant. Ids arrive from the client, so they are checked against the
    /// tenant's own catalog rather than inserted on trust — without this, a
    /// crafted request could attach another gym's services to a plan.
    /// </summary>
    private async Task<List<long>?> ValidServiceIdsAsync(long[] requested, CancellationToken ct)
    {
        var ids = requested.Distinct().ToArray();
        if (ids.Length == 0) return [];

        var found = await _db.PlanServices
            .Where(s => s.TenantId == Tenancy.TenantId && s.IsActive && ids.Contains(s.Id))
            .Select(s => s.Id)
            .ToListAsync(ct);

        return found.Count == ids.Length ? found : null;
    }

    /// <summary>
    /// Included services are current state, not history: editing a plan
    /// replaces the set. What a member actually bought is pinned on their
    /// `memberships` row, so nothing downstream depends on the old set.
    /// </summary>
    private async Task ReplaceServicesAsync(long planId, List<long> serviceIds, CancellationToken ct)
    {
        var existing = await _db.MembershipPlanServices
            .Where(s => s.PlanId == planId)
            .ToListAsync(ct);

        _db.MembershipPlanServices.RemoveRange(existing);
        _db.MembershipPlanServices.AddRange(serviceIds.Select(id =>
            new PlanServiceLink { PlanId = planId, ServiceId = id }));
    }

    // -----------------------------------------------------------------------
    // PUBLIC PROJECTION
    // -----------------------------------------------------------------------
    /// <summary>
    /// The plans as the marketing site shows them: active only, in the order
    /// the CMS set, with the service names spelled out rather than counted.
    ///
    /// It lives here rather than in the site module on purpose. Scoped() is the
    /// one place that knows what "a plan this branch sells" means — archived
    /// rows excluded, branch_id NULL meaning every branch. A second query over
    /// `membership_plans` written next door is how the public page ends up
    /// advertising a plan the desk can no longer sell.
    ///
    /// Note what the projection drops: PlanCode. It is an internal reference
    /// and has no business on a page anyone can read.
    /// </summary>
    public async Task<IReadOnlyList<PublicPlan>> PublicPlansAsync(CancellationToken ct) =>
        await Scoped()
            .Where(p => p.IsActive)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Price)
            .Select(p => new PublicPlan(
                p.Id,
                p.Name,
                p.Description,
                p.DurationValue,
                p.DurationUnit,
                p.Price,
                p.IsFeatured,
                p.PlanServices
                    .Where(s => s.Service != null)
                    .OrderBy(s => s.Service.DisplayOrder)
                    .ThenBy(s => s.Service.Name)
                    .Select(s => s.Service.Name)
                    .ToList()))
            .ToListAsync(ct);

    /// <summary>
    /// Accountability trail. The actor comes from the validated JWT, so it
    /// cannot be forged by the caller. Written for every mutation.
    /// </summary>
    private Task AuditAsync(string action, MembershipPlan plan, string description, CancellationToken ct)
    {
        _db.ActivityLogs.Add(new ActivityLog
        {
            TenantId = Tenancy.TenantId,
            BranchId = Tenancy.BranchId,
            ActorUserId = _actor.UserId,
            Action = action,
            EntityType = "membership_plan",
            EntityId = plan.Id == 0 ? null : plan.Id,
            Description = description,
            Metadata = JsonSerializer.Serialize(new { plan.PlanCode, plan.Price, plan.IsActive, plan.IsFeatured }),
        });
        return Task.CompletedTask;
    }

    private static MembershipPlanResponse ToResponse(MembershipPlan p) => new(
        p.Id,
        p.Name,
        p.PlanCode,
        p.Description,
        p.DurationValue,
        p.DurationUnit,
        p.Price,
        p.IsActive,
        p.IsFeatured,
        p.DisplayOrder,
        p.CreatedAt,
        p.PlanServices
            .Where(s => s.Service is not null)
            .OrderBy(s => s.Service.DisplayOrder)
            .ThenBy(s => s.Service.Name)
            .Select(s => new PlanServiceOption(s.Service.Id, s.Service.Name))
            .ToList());
}
