using GymApis.Dtos;
using GymApis.Models.Members;
using GymApis.Repos;
using GymApis.Services.Attendance;
using GymApis.Services.Members;
using GymApis.Services.Payments;
using Microsoft.EntityFrameworkCore;

namespace GymApis.Services.Dashboard;

/// <summary>
/// The dashboard: one read that composes what the other modules already count.
///
/// The rule this service is built around is that it must not recount anything.
/// Members' membership-state cards and the outstanding-balance total are
/// MemberService.StatsAsync; today's register is AttendanceService.StatsAsync.
/// Both derive from the same relations their own screens read
/// (`v_member_overview`, the roster query), so the dashboard cannot disagree
/// with the page a user lands on after clicking a card. Reimplementing either
/// count here would create exactly the drift the view was written to prevent —
/// and StatsAsync already carries a subtlety worth not re-deriving: the
/// membership-state counts are taken over active members only, while the money
/// is summed over everyone.
///
/// This month's revenue used to be computed here, back when no module owned
/// payments. One does now, so it is PaymentService.MonthlyCollectionAsync and
/// the Monthly Revenue card composes it like everything else — the payments
/// page and this card sum the same money, and two copies of that expression is
/// how they would come to disagree.
///
/// What is genuinely new here is only what no module owns yet: this month's
/// joiners, the expiring shortlist, and the activity feed.
/// </summary>
public class DashboardService : IDashboardService
{
    /// <summary>
    /// Rows in the expiring table. Ten, and the card scrolls its own body —
    /// ten rows unscrolled would run well past the donut and attendance cards
    /// stacked beside it and leave the dashboard lopsided.
    /// </summary>
    private const int ExpiringShortlistSize = 10;

    /// <summary>
    /// Names in the attendance not-marked list. Separate from the constant
    /// above, which the two lists used to share: raising the expiring table to
    /// ten would otherwise have doubled this card too, and its height is part
    /// of what keeps the sidebar level with the table next to it.
    /// </summary>
    private const int NotMarkedShortlistSize = 5;

    /// <summary>Entries in the activity feed.</summary>
    private const int FeedSize = 12;

    /// <summary>The window the "Expiring Soon" card and the table agree on.</summary>
    private const int ExpiryWindowDays = 7;

    private readonly GymDbContext _db;
    private readonly IMemberService _members;
    private readonly IAttendanceService _attendance;
    private readonly IPaymentService _payments;
    private readonly IBranchClock _clock;
    private readonly ICurrentUser _actor;

    public DashboardService(
        GymDbContext db,
        IMemberService members,
        IAttendanceService attendance,
        IPaymentService payments,
        IBranchClock clock,
        ICurrentUser actor)
    {
        _db = db;
        _members = members;
        _attendance = attendance;
        _payments = payments;
        _clock = clock;
        _actor = actor;
    }

    // -----------------------------------------------------------------------
    // SCOPE
    // -----------------------------------------------------------------------
    // Same shape as every other service: tenant + branch expressed once per
    // relation, so no query here can silently omit it.

    private IQueryable<MemberOverview> ScopedOverview() =>
        _db.MemberOverviews.Where(v => v.TenantId == Tenancy.TenantId && v.BranchId == Tenancy.BranchId);

    private IQueryable<Member> ScopedMembers() =>
        _db.Members.Where(m => m.TenantId == Tenancy.TenantId && m.BranchId == Tenancy.BranchId);

    // The activity log is branch-nullable — a tenant-level action (a plan, which
    // is not branch-scoped) writes a null branch_id. Filtering on branch would
    // silently drop those, so the feed is tenant-scoped and takes the branch
    // rows plus the tenant-wide ones.
    private IQueryable<Models.Common.ActivityLog> ScopedActivity() =>
        _db.ActivityLogs.Where(a =>
            a.TenantId == Tenancy.TenantId &&
            (a.BranchId == null || a.BranchId == Tenancy.BranchId));

    /// <summary>
    /// Whether the caller may see money totals. Read off the validated JWT via
    /// ICurrentUser — never a header or a body field the client controls.
    /// </summary>
    private bool CanSeeRevenue() =>
        _actor.Role is { } role && AuthPolicies.ViewRevenueRoles.Contains(role);

    // -----------------------------------------------------------------------
    // SUMMARY
    // -----------------------------------------------------------------------
    public async Task<DashboardResponse> SummaryAsync(CancellationToken ct)
    {
        // Sequential, not Task.WhenAll: GymDbContext is scoped and a DbContext
        // does not support concurrent operations. Every step below shares this
        // request's context, so parallelising them would throw rather than
        // speed anything up.
        var memberStats = await _members.StatsAsync(ct);
        var attendance = await AttendanceAsync(ct);
        var today = await _clock.TodayAsync(ct);

        var newThisMonth = await NewMembersThisMonthAsync(today, ct);
        var revenue = CanSeeRevenue() ? await _payments.MonthlyCollectionAsync(today, ct) : (decimal?)null;

        return new DashboardResponse(
            Kpis: new DashboardKpis(
                ActiveMembers:        memberStats.Active,
                NewMembersThisMonth:  newThisMonth,
                ExpiringSoon:         memberStats.ExpiringSoon,
                Expired:              memberStats.Expired,
                PendingPayments:      memberStats.PendingPayments,
                PendingPaymentsValue: CanSeeRevenue() ? memberStats.PendingPaymentsValue : null,
                MonthlyRevenue:       revenue),

            // The donut's total is the three slices summed, so the arcs close.
            // It is not TotalMembers: a member with no membership at all belongs
            // to none of these states and would leave a gap in the ring.
            Mix: new MembershipMix(
                Active:       memberStats.Active,
                ExpiringSoon: memberStats.ExpiringSoon,
                Expired:      memberStats.Expired,
                Total:        memberStats.Active + memberStats.ExpiringSoon + memberStats.Expired),

            Expiring:   await ExpiringAsync(ct),
            Attendance: attendance,
            Activity:   await ActivityAsync(ct));
    }

    // -----------------------------------------------------------------------
    // PIECES
    // -----------------------------------------------------------------------

    /// <summary>
    /// Members who joined in the current month, in the branch's own calendar.
    ///
    /// `joined_on` is a plain date, so this is date arithmetic rather than an
    /// instant range — but which month "now" falls in still has to come from
    /// the branch clock. On the 1st at 02:00 IST, UTC is still on the last day
    /// of the previous month.
    /// </summary>
    private Task<int> NewMembersThisMonthAsync(DateOnly today, CancellationToken ct)
    {
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        return ScopedMembers().CountAsync(m => m.JoinedOn >= monthStart, ct);
    }

    /// <summary>
    /// The expiring shortlist, from the same view the members list reads.
    ///
    /// Three clauses worth stating:
    ///  * `MembershipId != null` — a member who never bought anything has a null
    ///    days_remaining and does not belong on an expiry list.
    ///  * `MemberStatus == "active"` — matches the Expiring Soon and Expired
    ///    cards above the table, which StatsAsync counts over active members
    ///    only. Without it the table could show names the card did not count.
    ///  * no lower bound on days_remaining — already-expired memberships sort
    ///    first and are the most urgent rows on the screen.
    /// </summary>
    private async Task<IReadOnlyList<ExpiringMembershipItem>> ExpiringAsync(CancellationToken ct) =>
        await ScopedOverview()
            .Where(v => v.MemberStatus == "active"
                     && v.MembershipId != null
                     && v.DaysRemaining != null
                     && v.DaysRemaining <= ExpiryWindowDays)
            .OrderBy(v => v.EndDate)
            .ThenBy(v => v.FullName)
            .Take(ExpiringShortlistSize)
            .Select(v => new ExpiringMembershipItem(
                v.Id, v.FullName, v.PhotoUrl, v.PlanName,
                v.EndDate, v.DaysRemaining, v.MembershipState))
            .ToListAsync(ct);

    /// <summary>
    /// Today's register plus the names still outstanding.
    ///
    /// Two calls into AttendanceService rather than one: StatsAsync owns the
    /// four counts (and triggers the rate-limited closeout catch-up, so a
    /// server that was down overnight self-heals on a dashboard load exactly as
    /// it does on the attendance page), and RosterAsync supplies the rows. Both
    /// build the roster, which is one small query over an active-staff table —
    /// cheaper than duplicating the status derivation here, which is the one
    /// thing StatusFor() is supposed to own.
    /// </summary>
    private async Task<DashboardAttendance> AttendanceAsync(CancellationToken ct)
    {
        var stats = await _attendance.StatsAsync(date: null, role: null, ct);

        var outstanding = await _attendance.RosterAsync(
            new AttendanceQuery
            {
                Status = AttendanceEnums.NotMarked,
                Page = 1,
                PageSize = NotMarkedShortlistSize,
            }, ct);

        return new DashboardAttendance(
            stats.TotalStaff,
            stats.Present,
            stats.Absent,
            stats.NotMarked,
            outstanding.Items
                .Select(r => new StaffNotMarkedItem(r.StaffId, r.FullName, r.Role, r.PhotoUrl))
                .ToList());
    }

    /// <summary>
    /// The latest entries from `activity_log`, with the actor's email resolved.
    ///
    /// A left join, not an Include: ActivityLog has no navigation to User (the
    /// column is a nullable bigint with no FK-backed relationship in the model),
    /// and the actor is null for system-written rows such as the attendance
    /// closeout. `idx_activity_recent` covers the ordering.
    ///
    /// Nothing is redacted here beyond what the log already stores. Descriptions
    /// were composed by the writing module for exactly this feed, and the rows
    /// only reach an authenticated member of staff.
    /// </summary>
    private async Task<IReadOnlyList<ActivityItem>> ActivityAsync(CancellationToken ct) =>
        await ScopedActivity()
            .OrderByDescending(a => a.CreatedAt)
            .Take(FeedSize)
            .Select(a => new ActivityItem(
                a.Id,
                a.Action,
                a.EntityType,
                a.EntityId,
                a.Description,
                a.CreatedAt,
                _db.Users.Where(u => u.Id == a.ActorUserId).Select(u => u.Email).FirstOrDefault()))
            .ToListAsync(ct);
}
