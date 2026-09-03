using System.Text.Json;
using GymApis.Dtos;
using GymApis.Models.Common;
using GymApis.Models.Members;
using GymApis.Models.Messaging;
using GymApis.Repos;
using GymApis.Services.Attendance; // IBranchClock — "this month" at the gym
using GymApis.Services.Staff;      // ServiceResult<T>
using GymApis.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// `Membership` unqualified would bind to the sibling namespace
// GymApis.Services.Membership, not to the entity. Aliases say which is meant.
using MembershipEntity = GymApis.Models.Members.Membership;
using MembershipPlan = GymApis.Models.Membership.MembershipPlan;

namespace GymApis.Services.Members;

/// <summary>
/// Members: the people who buy what MembershipPlanService sells. Same shape as
/// the modules before it — one scoping helper, a DTO allow-list, code
/// generation with a retry, an audit row per mutation, soft delete.
///
/// What is specific to this module:
///
///  * A member is never sold anything twice in one row. `memberships` is dated
///    history and `payments` is a ledger, so create writes three rows and
///    renew writes two more. Nothing is ever rewritten.
///  * Money is asymmetric on purpose. The plan's price is snapshotted onto the
///    membership; paid and remaining are summed from the ledger every time they
///    are read. The schema is explicit that they must never be stored, or the
///    two can drift.
///  * A member is deactivated, not deleted. `status` is a reversible switch and
///    the Inactive list is built from it; erasure is a separate, more
///    privileged action and the only writer of `deleted_at`.
///    `payments.member_id` is ON DELETE RESTRICT — a member cannot be removed
///    out from under their own receipts.
/// </summary>
public class MemberService : IMemberService
{
    private const string CodePrefix = "MEM-";

    /// <summary>
    /// The pending-payments screen's filter value: "owes something", which is
    /// `pending` or `partial` together. Not a DB vocabulary word, so it is not
    /// in MemberEnums.PaymentStates — it is the union of two of them.
    /// </summary>
    private const string OwesMoney = "owing";

    private readonly GymDbContext _db;
    private readonly IPhotoStorage _photos;
    private readonly ICurrentUser _actor;
    private readonly IBranchClock _clock;
    private readonly ILogger<MemberService> _log;

    public MemberService(
        GymDbContext db, IPhotoStorage photos, ICurrentUser actor,
        IBranchClock clock, ILogger<MemberService> log)
    {
        _db = db;
        _photos = photos;
        _actor = actor;
        _clock = clock;
        _log = log;
    }

    // -----------------------------------------------------------------------
    // SCOPE
    // -----------------------------------------------------------------------
    /// <summary>
    /// The only place tenant + branch filtering is expressed. Every read and
    /// write path starts here, so no query can silently omit it — which also
    /// makes it the IDOR guard: another tenant's member id simply does not
    /// exist. Erased rows are excluded by the global query filter.
    /// </summary>
    private IQueryable<Member> Scoped() =>
        _db.Members.Where(m => m.TenantId == Tenancy.TenantId && m.BranchId == Tenancy.BranchId);

    /// <summary>The list view, scoped the same way. Read-only, keyless.</summary>
    private IQueryable<MemberOverview> ScopedOverview() =>
        _db.MemberOverviews.Where(v => v.TenantId == Tenancy.TenantId && v.BranchId == Tenancy.BranchId);

    // -----------------------------------------------------------------------
    // LIST
    // -----------------------------------------------------------------------
    public async Task<PagedResult<MemberListItem>> ListAsync(MemberQuery q, CancellationToken ct)
    {
        var rows = ScopedOverview();

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = $"%{q.Search.Trim()}%";
            rows = rows.Where(v =>
                EF.Functions.ILike(v.FullName, term) ||
                EF.Functions.ILike(v.Phone, term) ||
                EF.Functions.ILike(v.MemberCode, term) ||
                (v.Email != null && EF.Functions.ILike(v.Email, term)));
        }

        // Unrecognised values are ignored rather than erroring — a stale
        // bookmark with ?status=foo should show the list, not a 400.
        if (q.Status is not null && MemberEnums.MembershipStates.Contains(q.Status))
            rows = rows.Where(v => v.MembershipState == q.Status);

        if (q.MemberStatus is not null && MemberEnums.Statuses.Contains(q.MemberStatus))
            rows = rows.Where(v => v.MemberStatus == q.MemberStatus);

        if (q.PlanId is { } planId)
            rows = rows.Where(v => v.PlanId == planId);

        // How much of the membership in force has been settled. `pending` is
        // nothing paid and `partial` is some of it — the pending-payments screen
        // asks for either, which is what OwesMoney below expresses.
        //
        // ponytail: payment_status is computed by v_membership_balance from the
        // payments ledger, so this predicate cannot use an index — Postgres has
        // to build the aggregate before it can filter on it. At a few hundred
        // members the plan is a scan either way and the page is instant. If this
        // gym passes a few thousand, the fix is a materialised view refreshed on
        // payment write, not a stored balance column: the schema is deliberate
        // that paid and remaining are never stored, or the two can drift.
        //
        // ponytail: only the membership in force is visible here. The view's
        // LATERAL picks the latest membership with status IN ('active','expired'),
        // so an unpaid balance sitting on a cancelled, frozen or upcoming
        // membership is invisible to this list — exactly as it is already
        // invisible to the Pending Payments card that links here. The list and
        // the card agreeing is the property that matters; widening the window is
        // a change to the view, and to StatsAsync with it.
        if (q.PaymentStatus == OwesMoney)
            rows = rows.Where(v => v.PaymentStatus == "pending" || v.PaymentStatus == "partial");
        else if (q.PaymentStatus is not null && MemberEnums.PaymentStates.Contains(q.PaymentStatus))
            rows = rows.Where(v => v.PaymentStatus == q.PaymentStatus);

        // "This month" is the branch's month, not the server's. Asia/Kolkata is
        // +5:30, so on the 1st between 00:00 and 05:30 local, UTC still says
        // last month — and the dashboard's New Members card, which links here,
        // derives its count from the same clock. Both agreeing is the point.
        if (q.Joined is not null && MemberEnums.JoinedWindows.Contains(q.Joined))
        {
            var today = await _clock.TodayAsync(ct);
            var monthStart = new DateOnly(today.Year, today.Month, 1);
            rows = rows.Where(v => v.JoinedOn >= monthStart);
        }

        var total = await rows.CountAsync(ct);

        // Largest debt first for the work queue; by name everywhere else. An
        // unrecognised sort falls through to the default, so no caller that
        // omits the parameter — which is every existing one — changes.
        var ordered = q.Sort == "balance_desc"
            ? rows.OrderByDescending(v => v.BalanceAmount).ThenBy(v => v.FullName)
            : rows.OrderBy(v => v.FullName);

        var items = await ordered
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .Select(v => new MemberListItem(
                v.Id,
                v.MemberCode,
                v.FullName,
                v.Phone,
                v.Email,
                v.PhotoUrl,
                v.MemberStatus,
                v.PlanName,
                v.EndDate,
                v.DaysRemaining,
                v.MembershipState,
                v.PaidAmount,
                v.BalanceAmount,
                v.PaymentStatus,
                v.MembershipId,
                v.JoinedOn))
            .ToListAsync(ct);

        return new PagedResult<MemberListItem>(items, q.Page, q.PageSize, total);
    }

    // -----------------------------------------------------------------------
    // STATS  (the six cards above the table)
    // -----------------------------------------------------------------------
    public async Task<MemberStatsResponse> StatsAsync(CancellationToken ct)
    {
        // One round trip. Every figure comes from the same view the table reads,
        // so a card can never disagree with the rows underneath it.
        var rows = await ScopedOverview()
            .Select(v => new { v.MemberStatus, v.MembershipState, v.BalanceAmount })
            .ToListAsync(ct);

        var owing = rows.Where(r => r.BalanceAmount > 0).ToList();

        // The membership-state cards count only members who are themselves
        // active. Deactivating no longer cancels the membership — it is a
        // reversible switch — so a deactivated member keeps an unexpired
        // membership row and would otherwise be counted under "Active" while
        // appearing in the Inactive list. Money is counted over everyone:
        // a deactivated member still owes what they owe.
        var live = rows.Where(r => r.MemberStatus == "active").ToList();

        return new MemberStatsResponse(
            TotalMembers: rows.Count,
            Active: live.Count(r => r.MembershipState == "active"),
            ExpiringSoon: live.Count(r => r.MembershipState == "expiring_soon"),
            Expired: live.Count(r => r.MembershipState == "expired"),
            PendingPayments: owing.Count,
            PendingPaymentsValue: owing.Sum(r => r.BalanceAmount ?? 0),
            Inactive: rows.Count(r => r.MemberStatus != "active"));
    }

    // -----------------------------------------------------------------------
    // GET
    // -----------------------------------------------------------------------
    public async Task<MemberResponse?> GetAsync(long id, CancellationToken ct)
    {
        var member = await Scoped()
            .Include(m => m.Memberships).ThenInclude(ms => ms.Plan)
            .FirstOrDefaultAsync(m => m.Id == id, ct);

        if (member is null) return null;

        var history = await HistoryFor(member, ct);

        return new MemberResponse(
            member.Id,
            member.MemberCode,
            member.FullName,
            member.Phone,
            member.Email,
            member.Gender,
            member.DateOfBirth,
            member.Address,
            member.PhotoUrl,
            member.EmergencyContactName,
            member.EmergencyContactPhone,
            member.JoinedOn,
            member.Status,
            member.MarketingOptIn,
            member.Notes,
            member.CreatedAt,
            history.FirstOrDefault(h => h.Status is "active" or "upcoming"),
            history);
    }

    /// <summary>
    /// Purchase history, newest first, with paid and balance summed from the
    /// ledger rather than read off the membership row.
    /// </summary>
    private async Task<List<MembershipHistoryItem>> HistoryFor(Member member, CancellationToken ct)
    {
        var ids = member.Memberships.Select(ms => ms.Id).ToList();

        // The rows, not a sum: the detail screen lists the instalments behind a
        // balance, and summing them here as well would be a second source of a
        // number the ledger already owns.
        var payments = await _db.Payments
            .Where(p => p.MembershipId != null && ids.Contains(p.MembershipId.Value))
            .OrderByDescending(p => p.PaidAt)
            .ToListAsync(ct);

        var byMembership = payments
            .GroupBy(p => p.MembershipId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        return member.Memberships
            .OrderByDescending(ms => ms.StartDate)
            .ThenByDescending(ms => ms.Id)
            .Select(ms =>
            {
                var rows = byMembership.GetValueOrDefault(ms.Id) ?? [];
                // Only completed payments count toward the balance — the same
                // FILTER v_membership_balance applies.
                var paid = rows.Where(p => p.Status == "completed").Sum(p => p.Amount);
                return new MembershipHistoryItem(
                    ms.Id,
                    ms.PlanId,
                    ms.Plan?.Name ?? "—",
                    ms.StartDate,
                    ms.EndDate,
                    ms.PlanPrice,
                    ms.DiscountAmount,
                    ms.TotalAmount,
                    paid,
                    ms.TotalAmount - paid,
                    PaymentStatusFor(ms.TotalAmount, paid),
                    ms.Status,
                    ms.EndDate.DayNumber - today.DayNumber,
                    rows.Select(p => new PaymentLine(
                        p.Id, p.Amount, p.Method, p.ReferenceNo, p.PaidAt, p.Status, p.Notes)).ToList());
            })
            .ToList();
    }

    /// <summary>
    /// The same three-way rule `v_membership_balance` uses. Stated once, in C#,
    /// for the detail screen; the list reads the view's own column.
    /// </summary>
    private static string PaymentStatusFor(decimal total, decimal paid) =>
        paid >= total ? "paid" : paid > 0 ? "partial" : "pending";

    // -----------------------------------------------------------------------
    // CREATE
    // -----------------------------------------------------------------------
    public async Task<ServiceResult<MemberResponse>> CreateAsync(
        MemberWriteRequest req, IFormFile? photo, CancellationToken ct)
    {
        var plan = await ActivePlanAsync(req.PlanId, ct);
        if (plan is null)
            return ServiceResult<MemberResponse>.Fail("The selected plan is not available.");

        // The price is read from the plan, never from the request — otherwise a
        // crafted form could sell an Elite Annual for one rupee.
        var money = ValidateMoney(plan.Price, req.DiscountAmount, req.PaidAmount);
        if (money is not null) return ServiceResult<MemberResponse>.Fail(money);

        string? photoUrl = null;
        if (photo is not null)
        {
            var saved = await _photos.SaveAsync(photo, PhotoFolder.Member, ct);
            if (!saved.Succeeded) return ServiceResult<MemberResponse>.Fail(saved.Error!);
            photoUrl = saved.Url;
        }

        var member = new Member
        {
            TenantId = Tenancy.TenantId,
            BranchId = Tenancy.BranchId,
            PhotoUrl = photoUrl,
            CreatedByUserId = _actor.UserId,
        };
        Apply(req, member);

        // Generated codes race: two concurrent creates can pick the same number.
        // Retry on the unique violation rather than locking.
        for (var attempt = 1; ; attempt++)
        {
            member.MemberCode = await NextMemberCodeAsync(ct);
            _db.Members.Add(member);
            try
            {
                await _db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex) && attempt < 5)
            {
                _db.Entry(member).State = EntityState.Detached;
                _log.LogInformation("member_code {Code} taken, retrying (attempt {Attempt}).",
                    member.MemberCode, attempt);
            }
        }

        var membership = NewMembership(
            member.Id, plan, req.JoiningDate, req.ExpiryDate, req.DiscountAmount);
        _db.Memberships.Add(membership);
        await _db.SaveChangesAsync(ct);      // need the membership id for the payment

        RecordPayment(member.Id, membership.Id, req.PaidAmount, req.PaymentMethod, req.PaymentReferenceNo);

        Audit("member.created", member,
            $"{member.FullName} ({member.MemberCode}) joined {plan.Name}.",
            new { member.MemberCode, PlanId = plan.Id, membership.PlanPrice, membership.DiscountAmount });

        if (req.PaidAmount > 0)
            Audit("payment.recorded", member,
                $"Collected {req.PaidAmount:0.00} from {member.FullName} ({member.MemberCode}).",
                new { member.MemberCode, Amount = req.PaidAmount, Method = req.PaymentMethod });

        await _db.SaveChangesAsync(ct);

        return ServiceResult<MemberResponse>.Ok((await GetAsync(member.Id, ct))!);
    }

    // -----------------------------------------------------------------------
    // UPDATE
    // -----------------------------------------------------------------------
    /// <summary>
    /// Personal details and status only.
    ///
    /// Plan, price, discount and payment are deliberately NOT editable here.
    /// Changing what someone bought goes through <see cref="RenewAsync"/>,
    /// which writes a new dated row — so a receipt already handed to a member
    /// can always be reproduced, and the price snapshot stays honest.
    /// </summary>
    public async Task<ServiceResult<MemberResponse>> UpdateAsync(
        long id, MemberWriteRequest req, IFormFile? photo, bool removePhoto, CancellationToken ct)
    {
        var member = await Scoped().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (member is null) return ServiceResult<MemberResponse>.Missing();

        var oldPhoto = member.PhotoUrl;

        if (photo is not null)
        {
            var saved = await _photos.SaveAsync(photo, PhotoFolder.Member, ct);
            if (!saved.Succeeded) return ServiceResult<MemberResponse>.Fail(saved.Error!);
            member.PhotoUrl = saved.Url;
        }
        else if (removePhoto)
        {
            member.PhotoUrl = null;
        }

        Apply(req, member);

        Audit("member.updated", member,
            $"{member.FullName} ({member.MemberCode}) updated.",
            new { member.MemberCode, member.Status });

        await _db.SaveChangesAsync(ct);

        // Only once the row is safely written — otherwise a failed save would
        // leave the member pointing at a file that no longer exists.
        if (member.PhotoUrl != oldPhoto) _photos.Delete(oldPhoto, PhotoFolder.Member);

        return ServiceResult<MemberResponse>.Ok((await GetAsync(member.Id, ct))!);
    }

    // -----------------------------------------------------------------------
    // RENEW  (Manage Membership / "Renew / New")
    // -----------------------------------------------------------------------
    public async Task<ServiceResult<MemberResponse>> RenewAsync(
        long id, MembershipWriteRequest req, CancellationToken ct)
    {
        var member = await Scoped()
            .Include(m => m.Memberships)
            .FirstOrDefaultAsync(m => m.Id == id, ct);

        if (member is null) return ServiceResult<MemberResponse>.Missing();

        var plan = await ActivePlanAsync(req.PlanId, ct);
        if (plan is null)
            return ServiceResult<MemberResponse>.Fail("The selected plan is not available.");

        var money = ValidateMoney(plan.Price, req.DiscountAmount, req.PaidAmount);
        if (money is not null) return ServiceResult<MemberResponse>.Fail(money);

        // `idx_one_active_membership` is a partial UNIQUE on member_id WHERE
        // status = 'active'. Close the current row before inserting the next,
        // exactly as a staff shift change does — insert-then-close would trip
        // the index.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var current in member.Memberships.Where(ms => ms.Status == "active"))
            current.Status = current.EndDate < today ? "expired" : "cancelled";

        var membership = NewMembership(
            member.Id, plan, req.StartDate, req.ExpiryDate, req.DiscountAmount);
        membership.CancellationReason = null;
        _db.Memberships.Add(membership);
        await _db.SaveChangesAsync(ct);

        RecordPayment(member.Id, membership.Id, req.PaidAmount, req.PaymentMethod, req.PaymentReferenceNo,
            notes: Clean(req.Notes));

        Audit("membership.renewed", member,
            $"{member.FullName} ({member.MemberCode}) started {plan.Name}.",
            new { member.MemberCode, PlanId = plan.Id, membership.PlanPrice, membership.DiscountAmount });

        if (req.PaidAmount > 0)
            Audit("payment.recorded", member,
                $"Collected {req.PaidAmount:0.00} from {member.FullName} ({member.MemberCode}).",
                new { member.MemberCode, Amount = req.PaidAmount, Method = req.PaymentMethod });

        await _db.SaveChangesAsync(ct);

        return ServiceResult<MemberResponse>.Ok((await GetAsync(member.Id, ct))!);
    }

    // -----------------------------------------------------------------------
    // UPDATE MEMBERSHIP  (plan change, extension, expiry or discount correction)
    // -----------------------------------------------------------------------
    /// <summary>
    /// Edits an existing membership row in place. This is the one place a plan
    /// change, an extension and an expiry correction all land, because they are
    /// the same edit with different fields moved.
    ///
    /// What it deliberately cannot do is change money that has been collected.
    /// `payments` is a ledger; the way to correct a payment is another row, not
    /// a rewrite of the membership it was taken against. That is also why an
    /// edit which would drop the total below what has already been paid is
    /// refused rather than left to produce a negative balance.
    /// </summary>
    public async Task<ServiceResult<MemberResponse>> UpdateMembershipAsync(
        long memberId, long membershipId, MembershipUpdateRequest req, CancellationToken ct)
    {
        var member = await Scoped()
            .Include(m => m.Memberships)
            .FirstOrDefaultAsync(m => m.Id == memberId, ct);

        if (member is null) return ServiceResult<MemberResponse>.Missing();

        // Reached through the member, never by bare id: another member's
        // membership id simply does not resolve here.
        var membership = member.Memberships.FirstOrDefault(ms => ms.Id == membershipId);
        if (membership is null) return ServiceResult<MemberResponse>.Missing();

        var plan = await ActivePlanAsync(req.PlanId, ct);
        if (plan is null)
            return ServiceResult<MemberResponse>.Fail("The selected plan is not available.");

        if (req.DiscountAmount > plan.Price)
            return ServiceResult<MemberResponse>.Fail("Discount cannot exceed the membership price.");

        // A plan change is a re-sale, so the price is re-snapshotted from the
        // plan as it stands now. The old snapshot is not preserved — this row
        // *is* the current agreement; superseded ones are separate rows.
        var newTotal = plan.Price - req.DiscountAmount;

        var alreadyPaid = await _db.Payments
            .Where(p => p.MembershipId == membershipId && p.Status == "completed")
            .SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;

        if (newTotal < alreadyPaid)
        {
            return ServiceResult<MemberResponse>.Fail(
                $"This membership already has {alreadyPaid:0.00} collected against it. " +
                "Record a refund before reducing the total below that.");
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var status = req.Status ?? membership.Status;

        // idx_one_active_membership is a partial UNIQUE on member_id WHERE
        // status = 'active', so exactly one row may hold that slot.
        //
        // Both halves of the swap are UPDATEs on the same table, and EF gives no
        // ordering guarantee between two updates in one SaveChanges — if this
        // row claims 'active' before the other releases it, the index trips with
        // a 409. So the release is committed first, inside a transaction that
        // also covers the claim: two statements, one atomic swap.
        //
        // (RenewAsync only escapes this because its second half is an INSERT,
        // and EF does order updates before inserts.)
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        if (status == "active")
        {
            var others = member.Memberships
                .Where(ms => ms.Id != membershipId && ms.Status == "active")
                .ToList();

            if (others.Count > 0)
            {
                foreach (var other in others)
                    other.Status = other.EndDate < today ? "expired" : "cancelled";

                await _db.SaveChangesAsync(ct);
            }
        }

        membership.PlanId = plan.Id;
        membership.PlanPrice = plan.Price;
        membership.DiscountAmount = req.DiscountAmount;
        membership.StartDate = req.StartDate;
        membership.EndDate = req.ExpiryDate
            ?? ExpiryFor(req.StartDate, plan.DurationValue, plan.DurationUnit);
        membership.Status = status;
        // TotalAmount is generated by Postgres — never assigned here.

        if (status == "cancelled")
        {
            membership.CancelledAt ??= DateTimeOffset.UtcNow;
            membership.CancellationReason = Clean(req.CancellationReason) ?? membership.CancellationReason;
        }
        else
        {
            // Reinstating a cancelled row must not leave the cancellation behind.
            membership.CancelledAt = null;
            membership.CancellationReason = null;
        }

        Audit("membership.updated", member,
            $"{member.FullName} ({member.MemberCode}) moved to {plan.Name}.",
            new { member.MemberCode, PlanId = plan.Id, membership.PlanPrice, membership.DiscountAmount, membership.Status });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return ServiceResult<MemberResponse>.Ok((await GetAsync(member.Id, ct))!);
    }

    // -----------------------------------------------------------------------
    // RECORD PAYMENT  (the partial-payment flow)
    // -----------------------------------------------------------------------
    /// <summary>
    /// Adds one payment to an existing membership. Instalments are simply
    /// several of these — nothing about a part-paid membership is a different
    /// shape from a fully-paid one, because paid and remaining are always summed
    /// from this ledger rather than stored.
    /// </summary>
    public async Task<ServiceResult<MemberResponse>> AddPaymentAsync(
        long memberId, long membershipId, PaymentWriteRequest req, CancellationToken ct)
    {
        var member = await Scoped()
            .Include(m => m.Memberships)
            .FirstOrDefaultAsync(m => m.Id == memberId, ct);

        if (member is null) return ServiceResult<MemberResponse>.Missing();

        var membership = member.Memberships.FirstOrDefault(ms => ms.Id == membershipId);
        if (membership is null) return ServiceResult<MemberResponse>.Missing();

        var alreadyPaid = await _db.Payments
            .Where(p => p.MembershipId == membershipId && p.Status == "completed")
            .SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;

        var outstanding = membership.TotalAmount - alreadyPaid;

        if (outstanding <= 0)
            return ServiceResult<MemberResponse>.Fail("This membership is already paid in full.");

        if (req.Amount > outstanding)
        {
            return ServiceResult<MemberResponse>.Fail(
                $"That is more than the {outstanding:0.00} outstanding on this membership.");
        }

        _db.Payments.Add(new Payment
        {
            TenantId = Tenancy.TenantId,
            BranchId = Tenancy.BranchId,
            MemberId = member.Id,
            MembershipId = membership.Id,
            Purpose = "membership",
            Amount = req.Amount,
            Method = req.Method,
            // Cash has no transaction to reference; storing whatever the form
            // happened to hold would be a fabricated audit trail.
            ReferenceNo = req.Method == "cash" ? null : Clean(req.ReferenceNo),
            PaidAt = req.PaidAt ?? DateTimeOffset.UtcNow,
            Status = "completed",
            CollectedByUserId = _actor.UserId,
            Notes = Clean(req.Notes),
        });

        Audit("payment.recorded", member,
            $"Collected {req.Amount:0.00} from {member.FullName} ({member.MemberCode}).",
            new { member.MemberCode, Amount = req.Amount, Method = req.Method, MembershipId = membership.Id });

        await _db.SaveChangesAsync(ct);

        return ServiceResult<MemberResponse>.Ok((await GetAsync(member.Id, ct))!);
    }

    // -----------------------------------------------------------------------
    // ACTIVATE / DEACTIVATE
    // -----------------------------------------------------------------------
    /// <summary>
    /// Flips the member's own status. This replaced a soft delete that also set
    /// `deleted_at`: the client needs deactivated members to stay on the list
    /// under an Inactive filter, not to disappear from every screen.
    ///
    /// Deliberately non-destructive. It does NOT cancel the membership or any
    /// open PT assignment, because a switch that can be flipped back must not
    /// destroy rows it cannot restore — a re-activated member would otherwise
    /// come back with their plan silently cancelled.
    ///
    /// `deleted_at` now means one thing only: erased. <see cref="EraseAsync"/>
    /// is the only path that writes it, and the only thing that hides a row.
    /// </summary>
    public async Task<ServiceResult<bool>> SetStatusAsync(long id, string status, CancellationToken ct)
    {
        var member = await Scoped().FirstOrDefaultAsync(m => m.Id == id, ct);

        if (member is null) return ServiceResult<bool>.Missing();

        var previous = member.Status;
        if (previous == status) return ServiceResult<bool>.Ok(true);

        member.Status = status;

        Audit("member.status_changed", member,
            $"{member.FullName} ({member.MemberCode}) set to {status}.",
            new { member.MemberCode, From = previous, To = status });

        await _db.SaveChangesAsync(ct);

        return ServiceResult<bool>.Ok(true);
    }

    // -----------------------------------------------------------------------
    // ERASE (DPDP right to erasure)
    // -----------------------------------------------------------------------
    /// <summary>
    /// Overwrites personal data in place and destroys the photo, while the
    /// financial rows survive. That is the shape the two obligations force:
    /// personal data must go, accounting records must stay.
    ///
    /// `member_code` is preserved on purpose — it is the only handle the
    /// payments ledger has left, and dropping it would orphan the money.
    /// Irreversible: there is no copy of what was overwritten.
    /// </summary>
    public async Task<ServiceResult<bool>> EraseAsync(long id, CancellationToken ct)
    {
        // Erasing twice must be idempotent rather than a 404, so the query
        // filter is lifted here: the first erase set `deleted_at`, which hides
        // the row from every other read path including Scoped().
        var member = await _db.Members
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == Tenancy.TenantId && m.BranchId == Tenancy.BranchId)
            .FirstOrDefaultAsync(m => m.Id == id, ct);

        if (member is null) return ServiceResult<bool>.Missing();

        var photo = member.PhotoUrl;
        var code = member.MemberCode;

        member.FullName = "Deleted member";
        member.Phone = "0000000000";      // NOT NULL; no real number is all zeroes
        member.Email = null;
        member.Gender = null;
        member.DateOfBirth = null;
        member.Address = null;
        member.PhotoUrl = null;
        member.EmergencyContactName = null;
        member.EmergencyContactPhone = null;
        member.Notes = null;
        member.MarketingOptIn = false;
        member.DeletedAt ??= DateTimeOffset.UtcNow;
        member.Status = "inactive";

        // Anything still queued for this member must go with the personal data.
        // ON DELETE CASCADE only fires on a real delete, and erasure is an
        // overwrite — without this, a renewal reminder queued yesterday still
        // holds their name and address and would still be delivered.
        //
        // Terminal rows stay: their bodies are already NULL, and the record
        // that a message WAS sent is the audit trail, not the personal data.
        var dropped = await _db.MessageQueue
            .Where(m => m.MemberId == member.Id
                     && (m.Status == MessageStatuses.Pending || m.Status == MessageStatuses.Sending))
            .ExecuteDeleteAsync(ct);

        Audit("member.erased", member,
            $"Personal data erased for {code}.",
            new { MemberCode = code, QueuedMessagesDropped = dropped });

        await _db.SaveChangesAsync(ct);

        _photos.Delete(photo, PhotoFolder.Member);

        return ServiceResult<bool>.Ok(true);
    }

    // -----------------------------------------------------------------------
    // INTERNALS
    // -----------------------------------------------------------------------

    /// <summary>Copies only DTO fields onto the entity. Ids and tenancy stay server-owned.</summary>
    private static void Apply(MemberWriteRequest req, Member member)
    {
        member.FullName = req.FullName.Trim();
        member.Phone = req.Phone.Trim();
        member.Email = Clean(req.Email);
        member.Gender = Clean(req.Gender);
        member.DateOfBirth = req.DateOfBirth;
        member.Address = Clean(req.Address);
        member.EmergencyContactName = Clean(req.EmergencyContactName);
        member.EmergencyContactPhone = Clean(req.EmergencyContactPhone);
        member.JoinedOn = req.JoiningDate;
        member.Status = req.Status;
        // DPDP consent, and the only field on this form that is a legal
        // position rather than a fact about the person. Bound from an explicit
        // unticked checkbox; never defaulted true, never inferred.
        member.MarketingOptIn = req.MarketingOptIn;
        member.Notes = Clean(req.Notes);
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>
    /// The plan the caller asked for, if the tenant may actually sell it.
    /// Archived plans are hidden by the entity's global query filter; inactive
    /// ones are rejected here. Ids arrive from the client, so they are checked
    /// against the tenant's own catalog rather than used on trust.
    /// </summary>
    private Task<MembershipPlan?> ActivePlanAsync(long planId, CancellationToken ct) =>
        _db.MembershipPlans
            .Where(p => p.TenantId == Tenancy.TenantId &&
                        (p.BranchId == null || p.BranchId == Tenancy.BranchId) &&
                        p.IsActive)
            .FirstOrDefaultAsync(p => p.Id == planId, ct);

    /// <summary>
    /// The two ceilings the DTO cannot check, because the price deliberately is
    /// not in the request. Returns the message, or null when the money is sane.
    /// </summary>
    private static string? ValidateMoney(decimal price, decimal discount, decimal paid)
    {
        if (discount > price)
            return "Discount cannot exceed the membership price.";

        var total = price - discount;
        if (paid > total)
            return "Paid amount cannot exceed the total amount.";

        return null;
    }

    private MembershipEntity NewMembership(
        long memberId,
        MembershipPlan plan,
        DateOnly start,
        DateOnly? expiryOverride,
        decimal discount)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        return new MembershipEntity
        {
            TenantId = Tenancy.TenantId,
            BranchId = Tenancy.BranchId,
            MemberId = memberId,
            PlanId = plan.Id,
            StartDate = start,
            EndDate = expiryOverride ?? ExpiryFor(start, plan.DurationValue, plan.DurationUnit),
            // Snapshot. The plan's price may change later; this receipt must not.
            PlanPrice = plan.Price,
            DiscountAmount = discount,
            Status = start > today ? "upcoming" : "active",
            CreatedByUserId = _actor.UserId,
            // TotalAmount is generated by Postgres — never assigned here.
        };
    }

    /// <summary>
    /// The C# twin of the schema's plan_interval():
    ///     end_date = start_date + plan_interval(value, unit) - 1 day
    /// A one-month plan bought on the 1st ends on the 30th/31st, not on the 1st
    /// of the next month — otherwise every membership silently runs a day long.
    /// </summary>
    private static DateOnly ExpiryFor(DateOnly start, int value, string unit) => unit switch
    {
        "day" => start.AddDays(value).AddDays(-1),
        "week" => start.AddDays(value * 7).AddDays(-1),
        "month" => start.AddMonths(value).AddDays(-1),
        _ => start.AddMonths(value).AddDays(-1),
    };

    /// <summary>
    /// Stages a payment row, or none at all.
    ///
    /// `payments` has CHECK (amount &gt; 0): a member who has paid nothing yet
    /// has no payment row. "Pending" is the absence of a payment, not a zero
    /// one — which is also why the balance view can simply SUM.
    /// </summary>
    private void RecordPayment(
        long memberId, long membershipId, decimal amount, string method, string? reference, string? notes = null)
    {
        if (amount <= 0) return;

        _db.Payments.Add(new Payment
        {
            TenantId = Tenancy.TenantId,
            BranchId = Tenancy.BranchId,
            MemberId = memberId,
            MembershipId = membershipId,
            Purpose = "membership",
            Amount = amount,
            Method = method,
            // Cash has no transaction to reference; storing whatever the form
            // happened to hold would be a fabricated audit trail.
            ReferenceNo = method == "cash" ? null : Clean(reference),
            PaidAt = DateTimeOffset.UtcNow,
            Status = "completed",
            CollectedByUserId = _actor.UserId,
            Notes = notes,
        });
    }

    /// <summary>Next code in the MEM-0001 series. Racy by nature — the caller retries.</summary>
    private async Task<string> NextMemberCodeAsync(CancellationToken ct)
    {
        var codes = await _db.Members
            .IgnoreQueryFilters()      // a deleted member still owns their code
            .Where(m => m.TenantId == Tenancy.TenantId && m.MemberCode.StartsWith(CodePrefix))
            .Select(m => m.MemberCode)
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
    /// Accountability trail. The actor comes from the validated JWT, so it
    /// cannot be forged by the caller. Staged only — the caller's
    /// SaveChangesAsync commits it in the same transaction as the change.
    /// </summary>
    private void Audit(string action, Member member, string description, object metadata)
    {
        _db.ActivityLogs.Add(new ActivityLog
        {
            TenantId = Tenancy.TenantId,
            BranchId = Tenancy.BranchId,
            ActorUserId = _actor.UserId,
            Action = action,
            EntityType = "member",
            EntityId = member.Id == 0 ? null : member.Id,
            Description = description,
            Metadata = JsonSerializer.Serialize(metadata),
        });
    }
}
