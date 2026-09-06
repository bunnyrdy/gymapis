using GymApis.Dtos;
using GymApis.Models.Members;
using GymApis.Repos;
using GymApis.Services.Attendance;   // IBranchClock
using GymApis.Services.Members;
using Microsoft.EntityFrameworkCore;

namespace GymApis.Services.Payments;

/// <summary>
/// The centralized payments page.
///
/// Two things this service deliberately does NOT do:
///
///  * It does not recount. Total Outstanding and Members Owing come from
///    MemberService.StatsAsync, because the Pending Payments tab reads
///    GET /api/members — a card that counted separately would disagree with the
///    rows it opens. That is the drift v_member_overview exists to prevent, and
///    the same composition the dashboard already performs.
///
///  * It does not write. The nested member endpoint stays the one place a
///    payment is recorded, so there is one audit trail and one overpay check.
/// </summary>
public class PaymentService : IPaymentService
{
    private readonly GymDbContext _db;
    private readonly IMemberService _members;
    private readonly IBranchClock _clock;
    private readonly ICurrentUser _actor;

    public PaymentService(
        GymDbContext db, IMemberService members, IBranchClock clock, ICurrentUser actor)
    {
        _db = db;
        _members = members;
        _clock = clock;
        _actor = actor;
    }

    // -----------------------------------------------------------------------
    // SCOPE
    // -----------------------------------------------------------------------
    // Tenant + branch expressed once, so no query below can silently omit it.
    // This is also the IDOR guard: a payment belonging to another branch is not
    // reachable by guessing its id, because nothing here starts from an id.
    private IQueryable<PaymentLedger> Scoped() =>
        _db.PaymentLedger.Where(p => p.TenantId == Tenancy.TenantId && p.BranchId == Tenancy.BranchId);

    /// <summary>
    /// Whether the caller may see money totals. Read off the validated JWT via
    /// ICurrentUser — never a header or a body field the client controls.
    ///
    /// This is the ONLY gate in the module, and keeping it that way is what
    /// makes moving to an endpoint-level [Authorize(ViewRevenue)] a one-line
    /// change if the client ever wants the whole page restricted.
    /// </summary>
    private bool CanSeeRevenue() =>
        _actor.Role is { } role && AuthPolicies.ViewRevenueRoles.Contains(role);

    // -----------------------------------------------------------------------
    // THE WINDOW
    // -----------------------------------------------------------------------
    /// <summary>
    /// Resolves the query's date filters to a half-open instant range, or to
    /// (null, null) for the whole ledger.
    ///
    /// Precedence is most-specific-first: an explicit From/To beats a chosen
    /// month or year, which beats the tab's own range. So picking a custom range
    /// while the This Month tab is open shows the custom range — the filter bar
    /// wins over the tab, which is the way round a user expects.
    ///
    /// Every boundary is IBranchClock.StartOfDayAsync, and the comparison is
    /// always `paid_at >= from AND paid_at &lt; to`. Two reasons that shape is not
    /// negotiable: it is sargable against idx_payments_ledger, and casting
    /// paid_at to a branch-local date instead would be a STABLE expression
    /// Postgres cannot index. It is also why the ranges are half-open — a
    /// `&lt;= endOfDay` needs a "last instant of the day" that does not exist.
    ///
    /// The clock matters. Asia/Kolkata is +5:30, so between midnight and 05:30
    /// IST a UTC "today" still says yesterday — this month's first payment would
    /// land in last month's tab.
    /// </summary>
    private async Task<(DateTimeOffset? From, DateTimeOffset? To)> WindowAsync(
        PaymentQuery q, CancellationToken ct)
    {
        if (q.From is not null || q.To is not null)
        {
            // `To` is inclusive to the caller — a range ending on the 30th means
            // "through the 30th" — so the exclusive bound is the next day.
            var from = q.From is { } f ? await _clock.StartOfDayAsync(f, ct) : (DateTimeOffset?)null;
            var to = q.To is { } t ? await _clock.StartOfDayAsync(t.AddDays(1), ct) : (DateTimeOffset?)null;
            return (from, to);
        }

        if (q.Year is { } year)
        {
            // A month needs a year (PaymentQuery.Validate enforces it); a year
            // alone is the "previous year" filter and spans the whole year.
            var start = q.Month is { } month ? new DateOnly(year, month, 1) : new DateOnly(year, 1, 1);
            var end = q.Month is not null ? start.AddMonths(1) : start.AddYears(1);
            return (await _clock.StartOfDayAsync(start, ct), await _clock.StartOfDayAsync(end, ct));
        }

        // An unrecognised range falls through to the default, matching
        // MemberService.ListAsync's rule that a stale bookmark shows the list
        // rather than a 400.
        var range = q.Range is not null && PaymentEnums.Ranges.Contains(q.Range) ? q.Range : "this_month";
        if (range == "all") return (null, null);

        var today = await _clock.TodayAsync(ct);
        var monthStart = await _clock.StartOfDayAsync(new DateOnly(today.Year, today.Month, 1), ct);

        // "previous" is everything before this month began — the tab, as opposed
        // to the Month/Year filters which pick one specific past window.
        return range == "previous"
            ? (null, monthStart)
            : (monthStart, null);
    }

    /// <summary>Everything but paging and ordering. Shared by the list and its stats.</summary>
    private async Task<IQueryable<PaymentLedger>> FilteredAsync(PaymentQuery q, CancellationToken ct)
    {
        var rows = Scoped();

        var (from, to) = await WindowAsync(q, ct);
        if (from is { } f) rows = rows.Where(p => p.PaidAt >= f);
        if (to is { } t) rows = rows.Where(p => p.PaidAt < t);

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = $"%{q.Search.Trim()}%";
            rows = rows.Where(p =>
                EF.Functions.ILike(p.MemberName, term) ||
                EF.Functions.ILike(p.MemberCode, term) ||
                EF.Functions.ILike(p.Phone, term) ||
                (p.ReceiptNo != null && EF.Functions.ILike(p.ReceiptNo, term)));
        }

        // if (!string.IsNullOrWhiteSpace(q.Phone))
        //     rows = rows.Where(p => EF.Functions.ILike(p.Phone, $"%{q.Phone.Trim()}%"));

        // The MEMBERSHIP's payment status, not the transaction's. `owing` is the
        // union of pending and partial — someone who paid half still owes — and
        // is not in MemberEnums.PaymentStates for the same reason it is not in
        // MemberService: those three are the vocabulary v_membership_balance
        // derives, and `owing` is a filter over two of them. Expressed the same
        // way in both services so one dropdown can drive both.
        if (q.PaymentStatus == "owing")
            rows = rows.Where(p => p.PaymentStatus == "pending" || p.PaymentStatus == "partial");
        else if (q.PaymentStatus is not null && MemberEnums.PaymentStates.Contains(q.PaymentStatus))
            rows = rows.Where(p => p.PaymentStatus == q.PaymentStatus);

        if (q.PlanId is { } planId)
            rows = rows.Where(p => p.PlanId == planId);

        // The membership still owes something. Note this is the MEMBERSHIP's
        // balance, repeated across its instalments — so a membership paid in
        // three parts drops out of this filter once the third lands, and all
        // three of its rows go with it. That is correct: the filter answers
        // "which money is still outstanding", not "which payment was partial".
        if (q.BalanceDueOnly)
            rows = rows.Where(p => p.BalanceAmount > 0);

        return rows;
    }

    // -----------------------------------------------------------------------
    // LIST
    // -----------------------------------------------------------------------
    public async Task<PagedResult<PaymentLedgerItem>> ListAsync(PaymentQuery q, CancellationToken ct)
    {
        var rows = await FilteredAsync(q, ct);

        var total = await rows.CountAsync(ct);

        // Newest first, always. A ledger is read from the most recent
        // transaction backwards, and Id breaks the tie so paging is stable when
        // two payments share an instant (a renewal and its first instalment).
        var items = await rows
            .OrderByDescending(p => p.PaidAt)
            .ThenByDescending(p => p.Id)
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .Select(p => new PaymentLedgerItem(
                p.Id,
                p.ReceiptNo,
                p.MemberId,
                p.MemberCode,
                p.MemberName,
                p.Phone,
                p.MembershipId,
                p.PlanName,
                p.PaidAt,
                p.EndDate,
                p.Amount,
                p.BalanceAmount,
                p.Method,
                p.Status,
                p.PaymentStatus,
                p.ReferenceNo))
            .ToListAsync(ct);

        return new PagedResult<PaymentLedgerItem>(items, q.Page, q.PageSize, total);
    }

    // -----------------------------------------------------------------------
    // STATS  (the four cards above the table)
    // -----------------------------------------------------------------------
    public async Task<PaymentStatsResponse> StatsAsync(PaymentQuery q, CancellationToken ct)
    {
        // Outstanding is a standing figure, not a windowed one: money owed does
        // not belong to the month it was invoiced in, and the Pending Payments
        // tab this card opens spans every window too.
        var memberStats = await _members.StatsAsync(ct);

        var today = await _clock.TodayAsync(ct);
        var thisMonth = await MonthlyCollectionAsync(today, ct);

        // These two DO move with the filter bar — "cards should apply filters"
        // read in reverse: the count and value of whatever the table is
        // currently showing, so the figure always describes the rows below it.
        var rows = await FilteredAsync(q, ct);
        var received = await rows.CountAsync(ct);
        var receivedValue = await rows
            .Where(p => p.Status == "completed")
            .SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;

        var money = CanSeeRevenue();

        return new PaymentStatsResponse(
            ThisMonthCollection:   money ? thisMonth : null,
            TotalOutstanding:      money ? memberStats.PendingPaymentsValue : null,
            MembersOwing:          memberStats.PendingPayments,
            TotalPaymentsReceived: received,
            TotalPaymentsValue:    money ? receivedValue : null);
    }

    // -----------------------------------------------------------------------
    // MONTHLY COLLECTION  (also the dashboard's Monthly Revenue card)
    // -----------------------------------------------------------------------
    /// <summary>
    /// Completed payments taken this month. Moved here from DashboardService so
    /// the card and the page cannot drift; the dashboard now composes it, the
    /// same way it already composes MemberService.StatsAsync.
    ///
    /// Half-open instant range rather than a cast on paid_at: `paid_at >= start
    /// AND paid_at &lt; nextStart` is sargable against idx_payments_revenue, which
    /// schema_v1.sql built for exactly this query. Casting the column to a date
    /// in the branch timezone would ignore the index and, worse, would be a
    /// STABLE expression Postgres cannot index.
    ///
    /// Only `completed` counts. A pending or failed payment is not revenue, and
    /// a refund is a separate row that never became one — matching how
    /// v_membership_balance decides what has been paid.
    ///
    /// Reads `payments` directly rather than the ledger view: this is a sum over
    /// one column with no joins, and the view's four joins would buy nothing.
    /// </summary>
    public async Task<decimal> MonthlyCollectionAsync(DateOnly today, CancellationToken ct)
    {
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var from = await _clock.StartOfDayAsync(monthStart, ct);
        var to = await _clock.StartOfDayAsync(monthStart.AddMonths(1), ct);

        return await _db.Payments
            .Where(p => p.TenantId == Tenancy.TenantId && p.BranchId == Tenancy.BranchId)
            .Where(p => p.Status == "completed" && p.PaidAt >= from && p.PaidAt < to)
            .SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;
    }
}
