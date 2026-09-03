using System.Text.Json;
using GymApis.Dtos;
using GymApis.Models.Common;
using GymApis.Models.Staff;
using GymApis.Repos;
using GymApis.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GymApis.Services.Staff;

/// <summary>
/// Everything receptionists and trainers share — which is nearly all of it.
/// They live in the same `staff` table and differ by three constants and a
/// couple of fields, so the logic lives here once: tenant scoping, staff-code
/// generation, dated shift assignment, tag replacement, photo lifecycle,
/// auditing and soft delete.
///
/// A subclass supplies the role identity. That is the entire contract.
/// </summary>
public abstract class StaffServiceBase : IStaffService
{
    /// <summary>Value of `staff.role` — also the filter every query is scoped by.</summary>
    protected abstract string Role { get; }

    /// <summary>Prefix for the human-facing code: 'REC-' / 'TRN-'.</summary>
    protected abstract string CodePrefix { get; }

    /// <summary>Which `tags.category` this role picks from, if any.</summary>
    protected virtual string? TagCategory => null;

    private readonly GymDbContext _db;
    private readonly IPhotoStorage _photos;
    private readonly ICurrentUser _actor;
    private readonly ILogger _log;

    protected StaffServiceBase(GymDbContext db, IPhotoStorage photos, ICurrentUser actor, ILogger log)
    {
        _db = db;
        _photos = photos;
        _actor = actor;
        _log = log;
    }

    // -----------------------------------------------------------------------
    // LIST
    // -----------------------------------------------------------------------
    public async Task<PagedResult<StaffListItem>> ListAsync(StaffQuery q, CancellationToken ct)
    {
        var rows = Scoped();

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            // EF parameterises this — it never becomes string-concatenated SQL.
            var term = $"%{q.Search.Trim()}%";
            rows = rows.Where(s =>
                EF.Functions.ILike(s.FullName, term) ||
                EF.Functions.ILike(s.Phone, term) ||
                (s.Email != null && EF.Functions.ILike(s.Email, term)) ||
                EF.Functions.ILike(s.StaffCode, term));
        }

        // Ignore an unrecognised value rather than trusting it into the query.
        if (!string.IsNullOrWhiteSpace(q.Status) && StaffEnums.Statuses.Contains(q.Status))
            rows = rows.Where(s => s.Status == q.Status);

        if (!string.IsNullOrWhiteSpace(q.Gender) && StaffEnums.Genders.Contains(q.Gender))
            rows = rows.Where(s => s.Gender == q.Gender);

        if (q.ShiftId is { } shiftId)
            rows = rows.Where(s => s.ShiftAssignments.Any(a => a.EffectiveTo == null && a.ShiftId == shiftId));

        var total = await rows.CountAsync(ct);

        var items = await rows
            .OrderBy(s => s.FullName)
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .Select(s => new StaffListItem(
                s.Id,
                s.StaffCode,
                s.FullName,
                s.Gender,
                s.Phone,
                s.Email,
                s.PhotoUrl,
                s.Specialization,
                s.ExperienceYears,
                s.Status,
                s.ShiftAssignments.Where(a => a.EffectiveTo == null).Select(a => a.Shift.Name).FirstOrDefault(),
                s.ShiftAssignments.Where(a => a.EffectiveTo == null)
                    .Select(a => a.CustomStartTime ?? a.Shift.StartTime).FirstOrDefault(),
                s.ShiftAssignments.Where(a => a.EffectiveTo == null)
                    .Select(a => a.CustomEndTime ?? a.Shift.EndTime).FirstOrDefault(),
                _db.PtAssignments.Count(p => p.TrainerId == s.Id && p.Status == "active"),
                s.StaffTags.Select(t => t.Tag.Name).ToList()))
            .ToListAsync(ct);

        return new PagedResult<StaffListItem>(items, q.Page, q.PageSize, total);
    }

    // -----------------------------------------------------------------------
    // GET ONE
    // -----------------------------------------------------------------------
    public async Task<StaffResponse?> GetAsync(long id, CancellationToken ct)
    {
        var staff = await Scoped()
            .Include(s => s.StaffTags).ThenInclude(t => t.Tag)
            .Include(s => s.ShiftAssignments).ThenInclude(a => a.Shift)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        if (staff is null) return null;

        var ptClients = await _db.PtAssignments
            .CountAsync(p => p.TrainerId == staff.Id && p.Status == "active", ct);

        return ToResponse(staff, ptClients);
    }

    // -----------------------------------------------------------------------
    // CREATE
    // -----------------------------------------------------------------------
    public async Task<ServiceResult<StaffResponse>> CreateAsync(
        StaffWriteRequest req, IFormFile? photo, CancellationToken ct)
    {
        var tagIds = await ValidTagIdsAsync(req.ResponsibilityTagIds, ct);
        if (tagIds is null)
            return ServiceResult<StaffResponse>.Fail("One or more selections are not recognised.");

        if (req.ShiftId is { } shiftId && !await ShiftExistsAsync(shiftId, ct))
            return ServiceResult<StaffResponse>.Fail("The selected shift does not exist.");

        string? photoUrl = null;
        if (photo is not null)
        {
            var saved = await _photos.SaveAsync(photo, PhotoFolder.Staff, ct);
            if (!saved.Succeeded) return ServiceResult<StaffResponse>.Fail(saved.Error!);
            photoUrl = saved.Url;
        }

        // Tenant, branch and role are set here — never taken from the request.
        var staff = new StaffMember
        {
            TenantId = Tenancy.TenantId,
            BranchId = Tenancy.BranchId,
            Role = Role,
            PhotoUrl = photoUrl,
        };
        Apply(req, staff);

        // staff_code is UNIQUE (tenant_id, staff_code). Two admins clicking Save
        // at the same moment can compute the same next code, so retry on the
        // unique violation instead of pretending the race cannot happen.
        for (var attempt = 1; ; attempt++)
        {
            staff.StaffCode = await NextStaffCodeAsync(ct);
            _db.Staff.Add(staff);

            try
            {
                await _db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex) && attempt < 5)
            {
                _db.Entry(staff).State = EntityState.Detached;
                _log.LogInformation("staff_code collision on attempt {Attempt}; retrying.", attempt);
            }
        }

        await ReplaceTagsAsync(staff.Id, tagIds, ct);
        await ReplaceShiftAsync(staff, req, ct);
        await AuditAsync("staff.created", staff, $"{Role} {staff.FullName} ({staff.StaffCode}) added.", ct);
        await _db.SaveChangesAsync(ct);

        return ServiceResult<StaffResponse>.Ok((await GetAsync(staff.Id, ct))!);
    }

    // -----------------------------------------------------------------------
    // UPDATE
    // -----------------------------------------------------------------------
    public async Task<ServiceResult<StaffResponse>> UpdateAsync(
        long id, StaffWriteRequest req, IFormFile? photo, bool removePhoto, CancellationToken ct)
    {
        // Scoped() carries the tenant, branch, role and soft-delete filters, so
        // an id belonging to another tenant — or to a trainer when this is the
        // receptionist service — is simply not found. This is the IDOR guard,
        // and it lives in the query, not in an `if` someone can forget.
        var staff = await Scoped()
            .Include(s => s.ShiftAssignments)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        if (staff is null) return ServiceResult<StaffResponse>.Missing();

        var tagIds = await ValidTagIdsAsync(req.ResponsibilityTagIds, ct);
        if (tagIds is null)
            return ServiceResult<StaffResponse>.Fail("One or more selections are not recognised.");

        if (req.ShiftId is { } shiftId && !await ShiftExistsAsync(shiftId, ct))
            return ServiceResult<StaffResponse>.Fail("The selected shift does not exist.");

        var oldPhoto = staff.PhotoUrl;

        if (photo is not null)
        {
            var saved = await _photos.SaveAsync(photo, PhotoFolder.Staff, ct);
            if (!saved.Succeeded) return ServiceResult<StaffResponse>.Fail(saved.Error!);
            staff.PhotoUrl = saved.Url;
        }
        else if (removePhoto)
        {
            staff.PhotoUrl = null;
        }

        Apply(req, staff);

        await ReplaceTagsAsync(staff.Id, tagIds, ct);
        await ReplaceShiftAsync(staff, req, ct);
        await AuditAsync("staff.updated", staff, $"{Role} {staff.FullName} ({staff.StaffCode}) updated.", ct);
        await _db.SaveChangesAsync(ct);

        // Only bin the old file once the row that referenced it is committed.
        if (staff.PhotoUrl != oldPhoto) _photos.Delete(oldPhoto, PhotoFolder.Staff);

        return ServiceResult<StaffResponse>.Ok((await GetAsync(staff.Id, ct))!);
    }

    // -----------------------------------------------------------------------
    // DELETE (soft)
    // -----------------------------------------------------------------------
    public async Task<ServiceResult<bool>> DeleteAsync(long id, CancellationToken ct)
    {
        var staff = await Scoped().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (staff is null) return ServiceResult<bool>.Missing();

        // Soft delete, per the schema convention: attendance and payment history
        // reference this row, and a hard DELETE would either cascade that away or
        // fail on a foreign key.
        staff.DeletedAt = DateTimeOffset.UtcNow;
        staff.Status = "terminated";

        await AuditAsync("staff.deleted", staff, $"{Role} {staff.FullName} ({staff.StaffCode}) removed.", ct);
        await _db.SaveChangesAsync(ct);

        return ServiceResult<bool>.Ok(true);
    }

    // -----------------------------------------------------------------------
    // STATS  (the cards above the list)
    // -----------------------------------------------------------------------
    /// <summary>
    /// Counted over the whole branch for this role, via the same Scoped() every
    /// other read uses. The lists previously counted the twenty rows on screen
    /// and labelled the cards "on this page", which is a number that moves when
    /// you paginate.
    /// </summary>
    public async Task<StaffStatsResponse> StatsAsync(CancellationToken ct)
    {
        var rows = await Scoped()
            .Select(s => new
            {
                s.Status,
                PtClients = _db.PtAssignments.Count(pt => pt.TrainerId == s.Id && pt.Status == "active"),
            })
            .ToListAsync(ct);

        return new StaffStatsResponse(
            Total: rows.Count,
            Active: rows.Count(r => r.Status == "active"),
            Inactive: rows.Count(r => r.Status == "inactive"),
            OnLeave: rows.Count(r => r.Status == "on_leave"),
            // Always 0 for receptionists — they hold no PT assignments.
            WithPtClients: rows.Count(r => r.PtClients > 0));
    }

    // -----------------------------------------------------------------------
    // ERASE (DPDP right to erasure)
    // -----------------------------------------------------------------------
    /// <summary>
    /// Overwrites personal data in place and destroys the photo, while the
    /// employment record survives. Same shape as MemberService.EraseAsync and
    /// for the same reason: personal data must go, but attendance rows and
    /// `pt_assignments` reference this staff row and cannot be orphaned.
    ///
    /// `staff_code` is preserved deliberately — it is the handle those tables
    /// have left. Irreversible: nothing keeps a copy of what was overwritten.
    /// </summary>
    public async Task<ServiceResult<bool>> EraseAsync(long id, CancellationToken ct)
    {
        // Already-deleted staff must still be erasable, so the soft-delete
        // filter is lifted here — a delete is normally what happens first.
        var staff = await _db.Staff
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == Tenancy.TenantId &&
                        s.BranchId == Tenancy.BranchId &&
                        s.Role == Role)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        if (staff is null) return ServiceResult<bool>.Missing();

        var photo = staff.PhotoUrl;
        var code = staff.StaffCode;

        staff.FullName = "Deleted staff";
        staff.Phone = "0000000000";      // NOT NULL; no real number is all zeroes
        staff.Email = null;
        staff.Gender = null;
        staff.DateOfBirth = null;
        staff.Address = null;
        staff.PhotoUrl = null;
        staff.EmergencyContactName = null;
        staff.EmergencyContactPhone = null;
        staff.Notes = null;
        staff.DeletedAt ??= DateTimeOffset.UtcNow;
        staff.Status = "terminated";

        await AuditAsync("staff.erased", staff, $"Personal data erased for {code}.", ct);
        await _db.SaveChangesAsync(ct);

        _photos.Delete(photo, PhotoFolder.Staff);

        return ServiceResult<bool>.Ok(true);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// The single place tenant, branch and role scoping is expressed. Every read
    /// and write path starts here, so no query can accidentally omit it.
    /// </summary>
    private IQueryable<StaffMember> Scoped() =>
        _db.Staff.Where(s =>
            s.TenantId == Tenancy.TenantId &&
            s.BranchId == Tenancy.BranchId &&
            s.Role == Role);

    /// <summary>Copies the allow-listed fields. Note what it cannot touch: id, tenant, branch, role, staff code.</summary>
    private static void Apply(StaffWriteRequest req, StaffMember s)
    {
        s.FullName = req.FullName.Trim();
        s.Gender = req.Gender;
        s.DateOfBirth = req.DateOfBirth;
        s.Phone = req.Phone.Trim();
        s.Email = Clean(req.Email);
        s.Address = Clean(req.Address);
        s.EmergencyContactName = Clean(req.EmergencyContactName);
        s.EmergencyContactPhone = Clean(req.EmergencyContactPhone);
        s.JobTitle = Clean(req.JobTitle);
        s.Specialization = Clean(req.Specialization);
        s.Qualifications = req.Qualifications
            .Select(q => q.Trim())
            .Where(q => q.Length > 0)
            .Distinct()
            .ToArray();
        s.ExperienceYears = req.ExperienceYears;
        s.JoiningDate = req.JoiningDate;
        s.Status = req.Status;
        s.Notes = Clean(req.Notes);
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>Next code in the REC-0001 / TRN-0001 series. Racy by nature — the caller retries.</summary>
    private async Task<string> NextStaffCodeAsync(CancellationToken ct)
    {
        var codes = await _db.Staff
            .IgnoreQueryFilters()      // a deleted row still owns its code
            .Where(s => s.TenantId == Tenancy.TenantId && s.StaffCode.StartsWith(CodePrefix))
            .Select(s => s.StaffCode)
            .ToListAsync(ct);

        var highest = codes
            .Select(c => int.TryParse(c[CodePrefix.Length..], out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();

        return $"{CodePrefix}{highest + 1:D4}";
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// Returns null when any id is unknown or belongs to another tenant. Ids
    /// arrive from the client, so they are checked against the tenant's own tags
    /// rather than inserted on trust.
    /// </summary>
    private async Task<List<long>?> ValidTagIdsAsync(long[] requested, CancellationToken ct)
    {
        var ids = requested.Distinct().ToArray();
        if (ids.Length == 0) return [];
        if (TagCategory is null) return null;   // this role does not take tags

        var found = await _db.Tags
            .Where(t => t.TenantId == Tenancy.TenantId && t.Category == TagCategory && ids.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(ct);

        return found.Count == ids.Length ? found : null;
    }

    private Task<bool> ShiftExistsAsync(long shiftId, CancellationToken ct) =>
        _db.Shifts.AnyAsync(s => s.Id == shiftId && s.TenantId == Tenancy.TenantId && s.IsActive, ct);

    private async Task ReplaceTagsAsync(long staffId, List<long> tagIds, CancellationToken ct)
    {
        var existing = await _db.StaffTags.Where(t => t.StaffId == staffId).ToListAsync(ct);
        _db.StaffTags.RemoveRange(existing);
        _db.StaffTags.AddRange(tagIds.Select(id => new StaffTag { StaffId = staffId, TagId = id }));
    }

    /// <summary>
    /// Shift changes are dated, never overwritten: close the current assignment
    /// and open a new one. Rewriting the row in place would retroactively change
    /// what last month's attendance log claims the person was scheduled for.
    /// A partial unique index allows only one open assignment per staff member,
    /// so the close must happen before the insert.
    /// </summary>
    private async Task ReplaceShiftAsync(StaffMember staff, StaffWriteRequest req, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var current = await _db.StaffShiftAssignments
            .Where(a => a.StaffId == staff.Id && a.EffectiveTo == null)
            .FirstOrDefaultAsync(ct);

        if (req.ShiftId is not { } shiftId)
        {
            if (current is not null) current.EffectiveTo = today;
            return;
        }

        var days = req.WorkingDays.Distinct().Order().ToArray();

        // Nothing actually changed — leave the existing row and its start date alone.
        if (current is not null &&
            current.ShiftId == shiftId &&
            current.WorkingDays.OrderBy(d => d).SequenceEqual(days) &&
            current.CustomStartTime == req.CustomStartTime &&
            current.CustomEndTime == req.CustomEndTime)
            return;

        if (current is not null) current.EffectiveTo = today;

        _db.StaffShiftAssignments.Add(new StaffShiftAssignment
        {
            StaffId = staff.Id,
            ShiftId = shiftId,
            WorkingDays = days,
            CustomStartTime = req.CustomStartTime,
            CustomEndTime = req.CustomEndTime,
            EffectiveFrom = today,
        });
    }

    /// <summary>
    /// Accountability trail. The actor comes from the validated JWT, so it
    /// cannot be forged by the caller. Written for every mutation.
    /// </summary>
    private Task AuditAsync(string action, StaffMember staff, string description, CancellationToken ct)
    {
        _db.ActivityLogs.Add(new ActivityLog
        {
            TenantId = Tenancy.TenantId,
            BranchId = Tenancy.BranchId,
            ActorUserId = _actor.UserId,
            Action = action,
            EntityType = "staff",
            EntityId = staff.Id == 0 ? null : staff.Id,
            Description = description,
            Metadata = JsonSerializer.Serialize(new { staff.StaffCode, staff.Role, staff.Status }),
        });
        return Task.CompletedTask;
    }

    private static StaffResponse ToResponse(StaffMember s, int ptClientCount)
    {
        var current = s.ShiftAssignments.FirstOrDefault(a => a.EffectiveTo == null);

        return new StaffResponse(
            s.Id, s.StaffCode, s.Role, s.FullName, s.Gender, s.DateOfBirth, s.Phone, s.Email,
            s.Address, s.PhotoUrl, s.EmergencyContactName, s.EmergencyContactPhone,
            s.JobTitle, s.Specialization, s.Qualifications, s.ExperienceYears,
            s.JoiningDate, s.Status, s.Notes, ptClientCount,
            current is null ? null : new ShiftAssignmentResponse(
                current.ShiftId,
                current.Shift.Name,
                current.CustomStartTime ?? current.Shift.StartTime,
                current.CustomEndTime ?? current.Shift.EndTime,
                current.WorkingDays),
            s.StaffTags.Select(t => new TagResponse(t.Tag.Id, t.Tag.Name)).ToList());
    }
}
