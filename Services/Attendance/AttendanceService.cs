using System.Text.Json;
using GymApis.Dtos;
using GymApis.Models.Attendance;
using GymApis.Models.Common;
using GymApis.Repos;
using GymApis.Services.Staff;
using Microsoft.EntityFrameworkCore;

namespace GymApis.Services.Attendance;

/// <summary>
/// The shared Trainer/Receptionist attendance module.
///
/// Trainers and receptionists are the same `staff` table with a different role,
/// and their attendance is the same `staff_attendance` table, so this is one
/// service rather than two — the roster narrows by role when asked and shows
/// everyone when not.
///
/// The idea the whole module rests on: a row means "something was expected of
/// this person that day, and here is what happened". Days nobody was rostered
/// for get no row. That is why two of the four statuses the client sees are
/// derived rather than stored — see <see cref="StatusFor"/>.
/// </summary>
public class AttendanceService : IAttendanceService
{
    /// <summary>How far back a single sweep will reach. See close_out_attendance().</summary>
    private const int LookbackDays = 31;

    /// <summary>How stale the closeout may get before a read triggers one.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(15);

    private static DateTimeOffset _lastSweep = DateTimeOffset.MinValue;
    private static readonly SemaphoreSlim SweepGate = new(1, 1);

    private readonly GymDbContext _db;
    private readonly IBranchClock _clock;
    private readonly ICurrentUser _actor;
    private readonly ILogger<AttendanceService> _log;

    public AttendanceService(
        GymDbContext db, IBranchClock clock, ICurrentUser actor, ILogger<AttendanceService> log)
    {
        _db = db;
        _clock = clock;
        _actor = actor;
        _log = log;
    }

    // -----------------------------------------------------------------------
    // ROSTER
    // -----------------------------------------------------------------------
    public async Task<PagedResult<AttendanceRosterItem>> RosterAsync(AttendanceQuery q, CancellationToken ct)
    {
        await EnsureSweptAsync(ct);

        var date = q.Date ?? await _clock.TodayAsync(ct);
        var rows = await BuildRosterAsync(date, q.Search, q.Role, q.ShiftId, ct);

        if (!string.IsNullOrWhiteSpace(q.Status) && AttendanceEnums.ClientStatuses.Contains(q.Status))
            rows = rows.Where(r => r.Status == q.Status).ToList();

        var total = rows.Count;

        var page = rows
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .ToList();

        return new PagedResult<AttendanceRosterItem>(page, q.Page, q.PageSize, total);
    }

    public async Task<AttendanceStatsResponse> StatsAsync(DateOnly? date, string? role, CancellationToken ct)
    {
        await EnsureSweptAsync(ct);

        var day = date ?? await _clock.TodayAsync(ct);

        // Deliberately the same builder the rows come from. Counting with a
        // second, separate query is how a card ends up disagreeing with the
        // table beneath it.
        var rows = await BuildRosterAsync(day, search: null, role, shiftId: null, ct);

        return new AttendanceStatsResponse(
            TotalStaff: rows.Count,
            Present:    rows.Count(r => r.Status == AttendanceEnums.Present),
            Absent:     rows.Count(r => r.Status == AttendanceEnums.Absent),
            NotMarked:  rows.Count(r => r.Status == AttendanceEnums.NotMarked));
    }

    /// <summary>
    /// One query, then derivation in memory.
    ///
    /// Two of the four statuses do not exist in the database — "not marked" is
    /// the absence of a row, and "week off" is a day outside the person's
    /// roster — so they cannot be a SQL predicate without restating the roster
    /// rules a second time in LINQ. Deriving first and filtering afterwards
    /// keeps that logic in exactly one place. The set being loaded is one
    /// branch's active staff for one day, which is dozens of rows, not a table
    /// scan; the page size cap still bounds what leaves the API.
    /// </summary>
    private async Task<List<AttendanceRosterItem>> BuildRosterAsync(
        DateOnly date, string? search, string? role, long? shiftId, CancellationToken ct)
    {
        var staff = ScopedStaff();

        if (!string.IsNullOrWhiteSpace(search))
        {
            // EF parameterises this — it never becomes string-concatenated SQL.
            var term = $"%{search.Trim()}%";
            staff = staff.Where(s =>
                EF.Functions.ILike(s.FullName, term) ||
                EF.Functions.ILike(s.StaffCode, term) ||
                EF.Functions.ILike(s.Phone, term));
        }

        // Ignore an unrecognised value rather than trusting it into the query:
        // a stale bookmark should show the roster, not a 400.
        if (!string.IsNullOrWhiteSpace(role) && AttendanceEnums.Roles.Contains(role))
            staff = staff.Where(s => s.Role == role);

        var raw = await staff
            .OrderBy(s => s.FullName)
            .Select(s => new
            {
                s.Id,
                s.StaffCode,
                s.FullName,
                s.PhotoUrl,
                s.Role,
                s.JobTitle,
                s.JoiningDate,

                // The roster row in force on THAT date, not simply the open one.
                // Viewing last Tuesday must show the shift worked last Tuesday.
                // On a changeover day two rows match, hence the tie-break.
                Assignment = s.ShiftAssignments
                    .Where(a => a.EffectiveFrom <= date &&
                                (a.EffectiveTo == null || a.EffectiveTo >= date))
                    .OrderByDescending(a => a.EffectiveFrom)
                    .ThenByDescending(a => a.Id)
                    .Select(a => new
                    {
                        a.ShiftId,
                        ShiftName = a.Shift.Name,
                        Start = a.CustomStartTime ?? a.Shift.StartTime,
                        End = a.CustomEndTime ?? a.Shift.EndTime,
                        a.WorkingDays,
                    })
                    .FirstOrDefault(),

                Record = _db.StaffAttendance
                    .Where(r => r.StaffId == s.Id && r.AttendanceDate == date)
                    .Select(r => new
                    {
                        r.Id,
                        r.Status,
                        r.IsLate,
                        r.CheckInAt,
                        r.CheckOutAt,
                        r.MarkedVia,
                        r.Notes,
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var iso = IsoWeekday(date);

        return raw
            .Where(r => shiftId is null || r.Assignment != null && r.Assignment.ShiftId == shiftId)
            .Select(r => new AttendanceRosterItem(
                r.Id,
                r.StaffCode,
                r.FullName,
                r.PhotoUrl,
                r.Role,
                r.JobTitle,
                r.Assignment?.ShiftName,
                r.Assignment?.Start,
                r.Assignment?.End,
                StatusFor(r.Record?.Status, r.Assignment?.WorkingDays, iso, r.JoiningDate <= date),
                r.Record?.Id,
                r.Record?.CheckInAt,
                r.Record?.CheckOutAt,
                r.Record?.IsLate ?? false,
                r.Record?.MarkedVia,
                r.Record?.Notes))
            .ToList();
    }

    // -----------------------------------------------------------------------
    // DETAIL
    // -----------------------------------------------------------------------
    public async Task<AttendanceDetailResponse?> DetailAsync(long staffId, DateOnly? month, CancellationToken ct)
    {
        await EnsureSweptAsync(ct);

        var today = await _clock.TodayAsync(ct);
        var monthStart = Normalise(month ?? today);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        var staff = await ScopedStaff()
            .Where(s => s.Id == staffId)
            .Select(s => new
            {
                s.Id,
                s.StaffCode,
                s.FullName,
                s.PhotoUrl,
                s.Role,
                s.JobTitle,
                s.JoiningDate,
                s.Status,
                Assignment = s.ShiftAssignments
                    .Where(a => a.EffectiveTo == null)
                    .Select(a => new
                    {
                        ShiftName = a.Shift.Name,
                        Start = a.CustomStartTime ?? a.Shift.StartTime,
                        End = a.CustomEndTime ?? a.Shift.EndTime,
                        a.WorkingDays,
                    })
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);

        // Null here is an unknown id, another branch's id, or a soft-deleted
        // one — the caller cannot tell which, which is the point.
        if (staff is null) return null;

        var records = await ScopedRecords()
            .Where(r => r.StaffId == staffId &&
                        r.AttendanceDate >= monthStart &&
                        r.AttendanceDate <= monthEnd)
            .Select(r => new { r.Id, r.AttendanceDate, r.Status, r.IsLate, r.CheckInAt })
            .ToListAsync(ct);

        var byDate = records.ToDictionary(r => r.AttendanceDate);
        var workingDays = staff.Assignment?.WorkingDays;

        var days = new List<AttendanceDayItem>();
        for (var d = monthStart; d <= monthEnd; d = d.AddDays(1))
        {
            byDate.TryGetValue(d, out var rec);
            days.Add(new AttendanceDayItem(
                d,
                StatusFor(rec?.Status, workingDays, IsoWeekday(d), d >= staff.JoiningDate),
                rec?.IsLate ?? false,
                rec?.CheckInAt));
        }

        // The view already owns this arithmetic. Its JOIN is an inner one, so a
        // month with no rows yields nothing at all — zeros, not a 404.
        var summary = await _db.StaffAttendanceMonthly
            .Where(v => v.StaffId == staffId && v.Month == monthStart)
            .Select(v => new AttendanceSummaryResponse(
                (int)v.WorkingDays, (int)v.DaysPresent, (int)v.DaysAbsent, v.AttendancePct))
            .FirstOrDefaultAsync(ct)
            ?? new AttendanceSummaryResponse(0, 0, 0, null);

        byDate.TryGetValue(today, out var todayRec);

        return new AttendanceDetailResponse(
            staff.Id,
            staff.StaffCode,
            staff.FullName,
            staff.PhotoUrl,
            staff.Role,
            staff.JobTitle,
            staff.JoiningDate,
            staff.Status,
            staff.Assignment?.ShiftName,
            staff.Assignment?.Start,
            staff.Assignment?.End,
            workingDays ?? [],
            monthStart,
            today,
            // Today may fall outside the month being browsed, in which case the
            // record dictionary has no entry and this reads not_marked. The
            // client only uses it while viewing the current month.
            StatusFor(todayRec?.Status, workingDays, IsoWeekday(today), today >= staff.JoiningDate),
            todayRec?.Id,
            summary,
            days);
    }

    public async Task<ServiceResult<PagedResult<AttendanceLogItem>>> LogAsync(
        long staffId, AttendanceLogQuery q, CancellationToken ct)
    {
        await EnsureSweptAsync(ct);

        var exists = await ScopedStaff().AnyAsync(s => s.Id == staffId, ct);
        if (!exists) return ServiceResult<PagedResult<AttendanceLogItem>>.Missing();

        var rows = ScopedRecords().Where(r => r.StaffId == staffId);

        if (q.From is { } from) rows = rows.Where(r => r.AttendanceDate >= from);
        if (q.To is { } to) rows = rows.Where(r => r.AttendanceDate <= to);

        var total = await rows.CountAsync(ct);

        var items = await rows
            .OrderByDescending(r => r.AttendanceDate)
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .Select(r => new AttendanceLogItem(
                r.Id,
                r.AttendanceDate,
                r.Shift == null ? null : r.Shift.Name,
                r.CheckInAt,
                r.CheckOutAt,
                r.Status,
                r.IsLate,
                r.MarkedVia,
                r.Notes))
            .ToListAsync(ct);

        return ServiceResult<PagedResult<AttendanceLogItem>>.Ok(
            new PagedResult<AttendanceLogItem>(items, q.Page, q.PageSize, total));
    }

    // -----------------------------------------------------------------------
    // MARK
    // -----------------------------------------------------------------------
    public async Task<ServiceResult<AttendanceRecordResponse>> MarkAsync(
        MarkAttendanceRequest req, bool canBackdate, CancellationToken ct)
    {
        var today = await _clock.TodayAsync(ct);

        if (req.Date > today)
            return ServiceResult<AttendanceRecordResponse>.Fail(
                "Attendance cannot be marked for a future date.");

        // Marking today is front-desk work. Rewriting history is not, so a
        // back-dated mark needs the managerial policy even though the endpoint
        // itself is open to receptionists.
        if (req.Date < today && !canBackdate)
            return ServiceResult<AttendanceRecordResponse>.Denied(
                "Only an owner, admin or manager can record attendance for a past date.");

        // Scoped() is the IDOR guard: an id from another branch, another
        // tenant, or a soft-deleted row is simply not found.
        var staff = await ScopedStaff()
            .Where(s => s.Id == req.StaffId)
            .Select(s => new { s.Id, s.StaffCode, s.Role })
            .FirstOrDefaultAsync(ct);

        if (staff is null) return ServiceResult<AttendanceRecordResponse>.Missing();

        // Duplicate prevention, layer one. The UNIQUE (staff_id,
        // attendance_date) index is layer two and catches the race; the
        // GlobalExceptionHandler already maps that violation to a 409.
        var clash = await ScopedRecords()
            .AnyAsync(r => r.StaffId == req.StaffId && r.AttendanceDate == req.Date, ct);

        if (clash)
            return ServiceResult<AttendanceRecordResponse>.Clash(
                "Attendance for this date is already recorded. Edit the existing record instead.");

        var roster = await RosterOnAsync(req.StaffId, req.Date, ct);

        var record = new StaffAttendance
        {
            TenantId = Tenancy.TenantId,
            BranchId = Tenancy.BranchId,
            StaffId = staff.Id,
            AttendanceDate = req.Date,
            // Snapshotted, so a later roster change cannot rewrite what this
            // day claims the person worked.
            ShiftId = roster?.ShiftId,
            Status = req.Status,
            MarkedVia = AttendanceEnums.ViaManual,
            // From the validated token, never from the request body.
            MarkedByUserId = _actor.UserId,
            Notes = Clean(req.Notes),
        };

        if (req.Status == AttendanceEnums.Present && req.Date == today)
        {
            var now = DateTimeOffset.UtcNow;
            record.CheckInAt = now;
            record.IsLate = await IsLateAsync(now, roster?.Start, ct);
        }

        // Saved before the audit is written so the log can name the row it is
        // about. Adding both in one call leaves entity_id null, because the
        // identity value does not exist until the INSERT has run.
        _db.StaffAttendance.Add(record);
        await _db.SaveChangesAsync(ct);

        Audit("attendance.marked", record, staff.StaffCode, staff.Role,
            $"Marked {staff.StaffCode} {req.Status} on {req.Date:yyyy-MM-dd}.");
        await _db.SaveChangesAsync(ct);

        return ServiceResult<AttendanceRecordResponse>.Ok(ToResponse(record));
    }

    public async Task<ServiceResult<AttendanceRecordResponse>> CorrectAsync(
        long id, CorrectAttendanceRequest req, CancellationToken ct)
    {
        var record = await ScopedRecords().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (record is null) return ServiceResult<AttendanceRecordResponse>.Missing();

        var staff = await ScopedStaff()
            .Where(s => s.Id == record.StaffId)
            .Select(s => new { s.StaffCode, s.Role })
            .FirstAsync(ct);

        var previous = record.Status;
        var today = await _clock.TodayAsync(ct);

        record.Status = req.Status;
        record.Notes = Clean(req.Notes);

        if (req.Status == AttendanceEnums.Absent)
        {
            // An absence with an arrival time on it is a contradiction the log
            // would carry forever.
            record.CheckInAt = null;
            record.CheckOutAt = null;
            record.IsLate = false;
        }
        else if (record.CheckInAt is null && record.AttendanceDate == today)
        {
            var roster = await RosterOnAsync(record.StaffId, record.AttendanceDate, ct);
            var now = DateTimeOffset.UtcNow;
            record.CheckInAt = now;
            record.IsLate = await IsLateAsync(now, roster?.Start, ct);
        }

        // A corrected row is a human decision, whatever wrote it originally.
        record.MarkedVia = AttendanceEnums.ViaManual;
        record.MarkedByUserId = _actor.UserId;

        Audit("attendance.corrected", record, staff.StaffCode, staff.Role,
            $"Changed {staff.StaffCode} on {record.AttendanceDate:yyyy-MM-dd} " +
            $"from {previous} to {req.Status}.",
            previous);

        await _db.SaveChangesAsync(ct);

        return ServiceResult<AttendanceRecordResponse>.Ok(ToResponse(record));
    }

    // -----------------------------------------------------------------------
    // CLOSEOUT
    // -----------------------------------------------------------------------

    /// <summary>
    /// Calls close_out_attendance(). The date, timezone and midnight-rollover
    /// arithmetic lives in that function rather than here — see the rationale
    /// in 005_attendance_module.sql. It is one idempotent statement, so two
    /// callers racing cannot double-write.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        // SqlQuery requires the projected column to be named "Value".
        FormattableString sql =
            $@"SELECT close_out_attendance({Tenancy.TenantId}, {Tenancy.BranchId}, now(), {LookbackDays}) AS ""Value""";

        var written = await _db.Database.SqlQuery<int>(sql).FirstAsync(ct);

        _lastSweep = DateTimeOffset.UtcNow;

        if (written > 0)
            _log.LogInformation("Attendance closeout marked {Count} day(s) absent.", written);

        return written;
    }

    /// <summary>
    /// The read-path catch-up.
    ///
    /// The hourly worker is the normal trigger; this exists so a server that
    /// was stopped overnight closes the gap on the first page load instead of
    /// leaving a permanent hole in the log. Rate-limited, non-blocking if
    /// another caller is already sweeping, and it swallows its own failures on
    /// purpose: a closeout that cannot run is not a reason to fail the roster
    /// the user asked for.
    /// </summary>
    private async Task EnsureSweptAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _lastSweep < SweepInterval) return;
        if (!await SweepGate.WaitAsync(0, ct)) return;

        try
        {
            if (DateTimeOffset.UtcNow - _lastSweep < SweepInterval) return;
            await SweepAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Attendance closeout failed on the read path; serving the roster anyway.");
        }
        finally
        {
            SweepGate.Release();
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// The single place tenant and branch scoping is expressed for staff. Soft
    /// delete arrives from the global query filter on StaffMember.
    ///
    /// Only active staff appear. Someone inactive, on_leave or terminated is
    /// not absent — they are not expected — and including them would leave rows
    /// stuck on "not marked" that nobody can ever clear. It also keeps this in
    /// step with close_out_attendance(), which filters the same way.
    /// </summary>
    private IQueryable<Models.Staff.StaffMember> ScopedStaff() =>
        _db.Staff.Where(s =>
            s.TenantId == Tenancy.TenantId &&
            s.BranchId == Tenancy.BranchId &&
            s.Status == "active");

    /// <summary>
    /// Attendance rows, reachable only through a staff member the caller can
    /// already see. The join is what stops a record id from another branch — or
    /// belonging to a soft-deleted person — from being read or rewritten.
    /// </summary>
    private IQueryable<StaffAttendance> ScopedRecords() =>
        from r in _db.StaffAttendance
        join s in ScopedStaff() on r.StaffId equals s.Id
        where r.TenantId == Tenancy.TenantId && r.BranchId == Tenancy.BranchId
        select r;

    private sealed record RosterOn(long ShiftId, TimeOnly? Start, TimeOnly? End, short[] WorkingDays);

    /// <summary>The shift assignment in force for one person on one date, with the changeover tie-break.</summary>
    private Task<RosterOn?> RosterOnAsync(long staffId, DateOnly date, CancellationToken ct) =>
        _db.StaffShiftAssignments
            .Where(a => a.StaffId == staffId &&
                        a.EffectiveFrom <= date &&
                        (a.EffectiveTo == null || a.EffectiveTo >= date))
            .OrderByDescending(a => a.EffectiveFrom)
            .ThenByDescending(a => a.Id)
            .Select(a => new RosterOn(
                a.ShiftId,
                a.CustomStartTime ?? a.Shift.StartTime,
                a.CustomEndTime ?? a.Shift.EndTime,
                a.WorkingDays))
            .FirstOrDefaultAsync(ct)!;

    /// <summary>
    /// What the client should display for one person on one day.
    ///
    /// A stored row wins outright. Otherwise the question is whether anything
    /// was expected of them: a day off the roster, or a day before they were
    /// hired, is a week off — the bucket the UI greys out and the attendance
    /// percentage ignores. Everything else is simply not marked yet. The
    /// closeout turns those into real absences once the shift has ended, so
    /// "not marked" on a past working day means either that the sweep has not
    /// caught up or that the person is on a Flex shift, which has no
    /// end-of-day to close and must be marked by hand.
    /// </summary>
    private static string StatusFor(string? stored, short[]? workingDays, short isoWeekday, bool employed)
    {
        if (stored is not null) return stored;
        if (!employed) return AttendanceEnums.WeekOff;
        if (workingDays is null || workingDays.Length == 0) return AttendanceEnums.NotMarked;
        return workingDays.Contains(isoWeekday) ? AttendanceEnums.NotMarked : AttendanceEnums.WeekOff;
    }

    /// <summary>ISO weekday, 1=Mon..7=Sun — the same convention as `working_days`.</summary>
    private static short IsoWeekday(DateOnly date) => (short)(((int)date.DayOfWeek + 6) % 7 + 1);

    /// <summary>First day of the month a date falls in.</summary>
    private static DateOnly Normalise(DateOnly date) => new(date.Year, date.Month, 1);

    /// <summary>
    /// Late is measured against the branch's wall clock, not UTC. A Flex shift
    /// has no start time, so nobody on it is ever late.
    /// </summary>
    private async Task<bool> IsLateAsync(DateTimeOffset checkIn, TimeOnly? shiftStart, CancellationToken ct)
    {
        if (shiftStart is not { } start) return false;
        var local = await _clock.ToLocalAsync(checkIn, ct);
        return TimeOnly.FromDateTime(local) > start;
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>
    /// Accountability trail. The actor comes from the validated JWT, so it
    /// cannot be forged by the caller. Written for every mutation.
    /// </summary>
    private void Audit(
        string action, StaffAttendance record, string staffCode, string role,
        string description, string? previousStatus = null)
    {
        _db.ActivityLogs.Add(new ActivityLog
        {
            TenantId = Tenancy.TenantId,
            BranchId = Tenancy.BranchId,
            ActorUserId = _actor.UserId,
            Action = action,
            EntityType = "staff_attendance",
            EntityId = record.Id == 0 ? null : record.Id,
            Description = description,
            Metadata = JsonSerializer.Serialize(new
            {
                StaffCode = staffCode,
                Role = role,
                Date = record.AttendanceDate.ToString("yyyy-MM-dd"),
                Status = record.Status,
                From = previousStatus,
                record.MarkedVia,
            }),
        });
    }

    private static AttendanceRecordResponse ToResponse(StaffAttendance r) =>
        new(r.Id, r.StaffId, r.AttendanceDate, r.Status, r.IsLate,
            r.CheckInAt, r.CheckOutAt, r.MarkedVia, r.Notes);
}
