using System.ComponentModel.DataAnnotations;

namespace GymApis.Dtos;

/// <summary>
/// The payments module's vocabularies.
///
/// Deliberately NOT folded into MemberEnums. MemberEnums.PaymentStates
/// (paid | partial | pending) is what v_membership_balance DERIVES about a
/// membership; <see cref="Statuses"/> below is what a transaction row actually
/// STORES. Two vocabularies that share a word — the same trap members.status
/// and membership_state already set, and the reason a payments row carries
/// both.
/// </summary>
public static class PaymentEnums
{
    /// <summary>
    /// Mirrors the payments.status CHECK (schema_v1.sql:417).
    ///
    /// NOT a filter surface, and the ledger's Status dropdown deliberately does
    /// not use it. Every write path hard-codes "completed" (MemberService:647
    /// and :881, and the entity default at Payment.cs:39); there is no refund
    /// path and no failure path, so three of these four values can never match
    /// a row. The dropdown filters the MEMBERSHIP's payment status instead —
    /// MemberEnums.PaymentStates — which is the one that varies.
    ///
    /// Kept because it documents the constraint, and because a refund feature
    /// would need exactly this list.
    /// </summary>
    public static readonly string[] Statuses = ["completed", "pending", "failed", "refunded"];

    /// <summary>Mirrors the payments.purpose CHECK (schema_v1.sql:410).</summary>
    public static readonly string[] Purposes =
        ["membership", "personal_training", "registration", "merchandise", "other"];

    /// <summary>
    /// Windows the ledger can be narrowed to, all resolved against IBranchClock:
    ///
    ///   this_month — the default, and what the page opens on
    ///   previous   — everything before this month began
    ///   all        — the whole ledger
    ///
    /// A specific past month is Month + Year, and an arbitrary window is
    /// From + To; both take precedence over this. See PaymentQuery.
    /// </summary>
    public static readonly string[] Ranges = ["this_month", "previous", "all"];
}

/// <summary>
/// The payments list's filter surface. Every value is optional; the default
/// (no parameters at all) is this month, newest first.
/// </summary>
public class PaymentQuery : IValidatableObject
{
    /// <summary>Member name, member code or receipt number. One box, ILIKE.</summary>
    [StringLength(80)]
    public string? Search { get; set; }

    /// <summary>
    /// Its own parameter rather than part of Search: the front desk looks a
    /// payment up by the number on the phone in front of them, and folding it
    /// into a name search would make "9" match half the ledger.
    /// </summary>
    [StringLength(20)]
    public string? Phone { get; set; }

    /// <summary>this_month (default) | previous | all. See PaymentEnums.Ranges.</summary>
    public string? Range { get; set; }

    /// <summary>A specific past month, 1-12. Requires <see cref="Year"/>.</summary>
    [Range(1, 12)]
    public int? Month { get; set; }

    /// <summary>A specific year. Valid on its own — that is the "previous year" filter.</summary>
    [Range(2000, 2100)]
    public int? Year { get; set; }

    /// <summary>Custom range, inclusive of both ends in the branch's calendar.</summary>
    public DateOnly? From { get; set; }

    public DateOnly? To { get; set; }

    /// <summary>Only payments against a membership that still owes something.</summary>
    public bool BalanceDueOnly { get; set; }

    /// <summary>
    /// How much of the MEMBERSHIP behind this payment has been settled:
    /// paid | partial | pending, plus the union value `owing` (= partial or
    /// pending) the Pending Payments tab asks for. See MemberEnums.PaymentStates.
    ///
    /// Deliberately not the transaction's own status. That column is a constant
    /// — see PaymentEnums.Statuses — so a dropdown over it can only offer one
    /// option that matches anything. This one genuinely varies, and it means the
    /// same thing here as it does on MemberQuery.PaymentStatus, so the ledger
    /// tabs and the member-shaped tabs can share one control.
    /// </summary>
    public string? PaymentStatus { get; set; }

    public long? PlanId { get; set; }

    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    /// <summary>
    /// Hard ceiling, and it matters more here than on most lists: this
    /// projection carries members' names and phone numbers, so ?pageSize=1000000
    /// is a contact-list export and a denial of service in the same request.
    /// </summary>
    [Range(1, 100)]
    public int PageSize { get; set; } = 20;

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (From is { } from && To is { } to && to < from)
            yield return new("The end of the range cannot be before its start.", [nameof(To)]);

        // A year alone is the "previous year" filter and is fine. A month alone
        // is not: "March" of no particular year has no window to resolve to.
        if (Month is not null && Year is null)
            yield return new("A month filter needs a year.", [nameof(Year)]);
    }
}

/// <summary>
/// One row of the payments table — the ten columns the client asked for, plus
/// the two ids the row's own actions need.
///
/// MembershipId rides along for the same reason MemberListItem gained it: the
/// Record Payment dialog opens straight from the row, with no round trip to
/// find out what it is paying against.
///
/// DPDP: no email, no address, no notes. The ledger answers "who paid what,
/// when". A notes field can carry anything the desk typed about a member, and a
/// browsable, filterable list is the wrong place for it to surface.
/// </summary>
public record PaymentLedgerItem(
    long Id,
    string? ReceiptNo,
    long MemberId,
    string MemberCode,
    string MemberName,
    string Phone,
    long? MembershipId,
    string? PlanName,
    DateTimeOffset PaidAt,
    DateOnly? EndDate,
    decimal Amount,
    decimal? BalanceAmount,
    string Method,
    // Both statuses ride along, because they answer different questions and the
    // screen needs the second one. `Status` is this transaction's own state and
    // is always "completed" today; `PaymentStatus` is how much of the membership
    // behind it has been settled, which is what the Status column renders and
    // what the filter narrows on.
    string Status,
    string? PaymentStatus,
    string? ReferenceNo);

/// <summary>
/// The four cards above the payments table.
///
/// TotalOutstanding and MembersOwing are MemberService.StatsAsync verbatim, not
/// recounted here — the Pending Payments tab reads GET /api/members, so a card
/// that counted differently would disagree with the rows it opens. That is the
/// drift v_member_overview exists to prevent, and the same rule the dashboard
/// follows.
///
/// The nullable fields follow the dashboard's split exactly: a count is a work
/// queue and everyone sees it; a total is a business figure and only
/// AuthPolicies.ViewRevenueRoles do. The client renders whatever arrived and
/// must never re-decide who may see revenue — that rule lives in one place.
/// </summary>
public record PaymentStatsResponse(
    decimal? ThisMonthCollection,
    decimal? TotalOutstanding,
    int MembersOwing,
    int TotalPaymentsReceived,
    decimal? TotalPaymentsValue);
