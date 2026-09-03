using System.ComponentModel.DataAnnotations;

namespace GymApis.Dtos;

/// <summary>
/// The vocabularies the DB CHECK constraints enforce. Kept here so validation
/// rejects bad input with a 400 rather than letting Postgres answer with a 500.
/// </summary>
public static class MemberEnums
{
    public static readonly string[] Genders = ["male", "female", "other", "undisclosed"];

    /// <summary>Note: NOT the staff vocabulary. `members` has its own CHECK.</summary>
    public static readonly string[] Statuses = ["active", "inactive", "frozen", "banned"];

    public static readonly string[] PaymentMethods = ["cash", "card", "bank_transfer", "upi", "other"];

    /// <summary>Filter values accepted by the list's status dropdown.</summary>
    public static readonly string[] MembershipStates = ["active", "expiring_soon", "expired", "no_membership"];

    /// <summary>A membership row's own status. Mirrors the memberships CHECK.</summary>
    public static readonly string[] MembershipStatuses = ["active", "expired", "cancelled", "frozen", "upcoming"];

    /// <summary>
    /// How much of the membership in force has been paid. Derived by
    /// v_membership_balance from the ledger, never stored — which is why this
    /// is a filter vocabulary and not a column CHECK.
    /// </summary>
    public static readonly string[] PaymentStates = ["paid", "partial", "pending"];

    /// <summary>Joining-date windows the list can be narrowed to.</summary>
    public static readonly string[] JoinedWindows = ["this_month"];

    /// <summary>
    /// Orderings the list offers. "name" is the default and what every screen
    /// but the pending-payments work queue uses.
    /// </summary>
    public static readonly string[] Sorts = ["name", "balance_desc"];
}

/// <summary>
/// Everything the Add / Edit Member form may set, and nothing else.
///
/// The DTO is the allow-list. It deliberately omits Id, TenantId, BranchId,
/// MemberCode, UserId, PhotoUrl and CreatedByUserId — all server-owned — and
/// every derived money field (TotalAmount, RemainingBalance, PaymentStatus),
/// which are computed from the plan price and the payments ledger. Binding a
/// request straight to the entity would hand a caller all of them.
///
/// Cross-field rules that need only these fields live in <see cref="Validate"/>.
/// The rules that need the plan's price — discount and paid amount ceilings —
/// cannot live here, because the price is deliberately not in the request.
/// MemberService checks those against the plan it loads.
/// </summary>
public class MemberWriteRequest : IValidatableObject
{
    // -- personal ------------------------------------------------------------
    [Required]
    [StringLength(120, MinimumLength = 2)]
    public string FullName { get; set; } = default!;

    [Required]
    [RegularExpression(@"^\d{10}$", ErrorMessage = "Phone number must contain exactly 10 digits.")]
    public string Phone { get; set; } = default!;

    [EmailAddress]
    [StringLength(160)]
    public string? Email { get; set; }

    public string? Gender { get; set; }

    public DateOnly? DateOfBirth { get; set; }

    [StringLength(300)]
    public string? Address { get; set; }

    [StringLength(120)]
    public string? EmergencyContactName { get; set; }

    [RegularExpression(@"^\d{10}$", ErrorMessage = "Emergency contact phone must contain exactly 10 digits.")]
    public string? EmergencyContactPhone { get; set; }

    public string Status { get; set; } = "active";

    /// <summary>
    /// DPDP consent for marketing mail (offers and promotions). It does NOT
    /// cover renewal reminders: those are service messages about a contract
    /// this member signed and need no separate permission — which is also why
    /// a reminder must never carry an offer in its body.
    ///
    /// Defaults to false and the form's checkbox ships unticked, because a
    /// pre-ticked consent box is not consent.
    /// </summary>
    public bool MarketingOptIn { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }

    // -- membership (create only; ignored on update) -------------------------
    /// <summary>
    /// The plan being sold. Only read on create — changing what someone bought
    /// goes through POST /api/members/{id}/memberships, which snapshots a fresh
    /// price and writes a new dated row instead of rewriting history.
    /// </summary>
    public long PlanId { get; set; }

    [Required]
    public DateOnly JoiningDate { get; set; }

    /// <summary>The form's "Override Date" pencil. Null = compute from the plan.</summary>
    public DateOnly? ExpiryDate { get; set; }

    [Range(0, 9_999_999)]
    public decimal DiscountAmount { get; set; }

    // -- payment (create only) -----------------------------------------------
    [Range(0, 9_999_999)]
    public decimal PaidAmount { get; set; }

    public string PaymentMethod { get; set; } = "cash";

    /// <summary>UPI ref / txn id / cheque no. Optional, and dropped for cash.</summary>
    [StringLength(60)]
    public string? PaymentReferenceNo { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (Gender is not null && !MemberEnums.Genders.Contains(Gender))
            yield return new($"Gender must be one of: {string.Join(", ", MemberEnums.Genders)}.", [nameof(Gender)]);

        if (!MemberEnums.Statuses.Contains(Status))
            yield return new($"Status must be one of: {string.Join(", ", MemberEnums.Statuses)}.", [nameof(Status)]);

        if (!MemberEnums.PaymentMethods.Contains(PaymentMethod))
            yield return new($"Payment method must be one of: {string.Join(", ", MemberEnums.PaymentMethods)}.", [nameof(PaymentMethod)]);

        if (DateOfBirth is { } dob)
        {
            if (dob >= today)
                yield return new("Date of birth must be in the past.", [nameof(DateOfBirth)]);
            // A gym may sign up a minor with a guardian; 12 is the floor that
            // catches a mistyped year without refusing real junior members.
            else if (dob > today.AddYears(-12))
                yield return new("Members must be at least 12 years old.", [nameof(DateOfBirth)]);
            else if (dob < today.AddYears(-100))
                yield return new("Date of birth looks incorrect.", [nameof(DateOfBirth)]);
        }

        if (JoiningDate > today.AddYears(1))
            yield return new("Joining date cannot be more than a year in the future.", [nameof(JoiningDate)]);
        if (JoiningDate < today.AddYears(-60))
            yield return new("Joining date looks incorrect.", [nameof(JoiningDate)]);

        if (ExpiryDate is { } expiry && expiry < JoiningDate)
            yield return new("Expiry date cannot be before the joining date.", [nameof(ExpiryDate)]);
    }
}

/// <summary>
/// Renewing, or selling a first membership to an existing member. JSON, not
/// multipart — no photo is involved.
/// </summary>
public class MembershipWriteRequest : IValidatableObject
{
    [Required]
    public long PlanId { get; set; }

    [Required]
    public DateOnly StartDate { get; set; }

    /// <summary>Null = compute from the plan's duration.</summary>
    public DateOnly? ExpiryDate { get; set; }

    [Range(0, 9_999_999)]
    public decimal DiscountAmount { get; set; }

    [Range(0, 9_999_999)]
    public decimal PaidAmount { get; set; }

    public string PaymentMethod { get; set; } = "cash";

    [StringLength(60)]
    public string? PaymentReferenceNo { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (!MemberEnums.PaymentMethods.Contains(PaymentMethod))
            yield return new($"Payment method must be one of: {string.Join(", ", MemberEnums.PaymentMethods)}.", [nameof(PaymentMethod)]);

        if (StartDate > today.AddYears(1))
            yield return new("Start date cannot be more than a year in the future.", [nameof(StartDate)]);

        if (ExpiryDate is { } expiry && expiry < StartDate)
            yield return new("Expiry date cannot be before the start date.", [nameof(ExpiryDate)]);
    }
}

/// <summary>
/// Editing an existing membership: a plan change, an extension, a corrected
/// expiry date, a renegotiated discount.
///
/// Money already collected is NOT here. Payments are a ledger — the way to
/// change what somebody has paid is to add another row, never to rewrite the
/// membership they paid against.
/// </summary>
public class MembershipUpdateRequest : IValidatableObject
{
    [Required]
    public long PlanId { get; set; }

    [Required]
    public DateOnly StartDate { get; set; }

    /// <summary>Null = recompute from the plan's duration.</summary>
    public DateOnly? ExpiryDate { get; set; }

    [Range(0, 9_999_999)]
    public decimal DiscountAmount { get; set; }

    /// <summary>active | expired | cancelled | frozen | upcoming. Null = leave alone.</summary>
    public string? Status { get; set; }

    [StringLength(1000)]
    public string? CancellationReason { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (Status is not null && !MemberEnums.MembershipStatuses.Contains(Status))
            yield return new($"Status must be one of: {string.Join(", ", MemberEnums.MembershipStatuses)}.", [nameof(Status)]);

        if (StartDate > today.AddYears(1))
            yield return new("Start date cannot be more than a year in the future.", [nameof(StartDate)]);

        if (StartDate < today.AddYears(-60))
            yield return new("Start date looks incorrect.", [nameof(StartDate)]);

        // Mirrors the table's own CHECK (end_date >= start_date), so a bad range
        // is a 400 rather than a 500 out of Postgres.
        if (ExpiryDate is { } expiry && expiry < StartDate)
            yield return new("Expiry date cannot be before the start date.", [nameof(ExpiryDate)]);
    }
}

/// <summary>
/// One payment against an existing membership — the partial-payment flow.
/// </summary>
public class PaymentWriteRequest : IValidatableObject
{
    /// <summary>
    /// Must be positive: `payments` has CHECK (amount > 0), so a zero payment
    /// cannot be stored at all. "Pending" is the absence of a row.
    /// </summary>
    [Range(0.01, 9_999_999)]
    public decimal Amount { get; set; }

    public string Method { get; set; } = "cash";

    [StringLength(60)]
    public string? ReferenceNo { get; set; }

    /// <summary>Null = now. Lets the desk back-date a payment taken earlier.</summary>
    public DateTimeOffset? PaidAt { get; set; }

    [StringLength(1000)]
    public string? Notes { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (!MemberEnums.PaymentMethods.Contains(Method))
            yield return new($"Payment method must be one of: {string.Join(", ", MemberEnums.PaymentMethods)}.", [nameof(Method)]);

        if (PaidAt is { } paidAt && paidAt > DateTimeOffset.UtcNow.AddDays(1))
            yield return new("A payment cannot be dated in the future.", [nameof(PaidAt)]);
    }
}

// ---------------------------------------------------------------------------
// Read models
// ---------------------------------------------------------------------------

/// <summary>One transaction against a membership. The ledger, not a summary.</summary>
public record PaymentLine(
    long Id,
    decimal Amount,
    string Method,
    string? ReferenceNo,
    DateTimeOffset PaidAt,
    string Status,
    string? Notes);

/// <summary>One row of the member's purchase history.</summary>
public record MembershipHistoryItem(
    long Id,
    long PlanId,
    string PlanName,
    DateOnly StartDate,
    DateOnly EndDate,
    decimal PlanPrice,
    decimal DiscountAmount,
    decimal TotalAmount,
    decimal PaidAmount,
    decimal BalanceAmount,
    // PaymentStatus is paid | partial | pending — derived from the ledger, never stored.
    string PaymentStatus,
    string Status,
    int DaysRemaining,
    // The instalments behind PaidAmount, newest first.
    IReadOnlyList<PaymentLine> Payments);

public record MemberResponse(
    long Id,
    string MemberCode,
    string FullName,
    string Phone,
    string? Email,
    string? Gender,
    DateOnly? DateOfBirth,
    string? Address,
    string? PhotoUrl,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    DateOnly JoinedOn,
    string Status,
    bool MarketingOptIn,
    string? Notes,
    DateTimeOffset CreatedAt,
    MembershipHistoryItem? CurrentMembership,
    IReadOnlyList<MembershipHistoryItem> History);

public record MemberListItem(
    long Id,
    string MemberCode,
    string FullName,
    string Phone,
    string? Email,
    string? PhotoUrl,
    string MemberStatus,
    string? PlanName,
    DateOnly? EndDate,
    int? DaysRemaining,
    string MembershipState,
    decimal? PaidAmount,
    decimal? BalanceAmount,
    string? PaymentStatus,
    // Appended for the pending-payments list: MembershipId is what lets a row
    // open the Record Payment dialog without first loading the member, and
    // JoinedOn backs the "joined this month" filter.
    long? MembershipId,
    DateOnly? JoinedOn);

/// <summary>
/// The six cards above the members table.
///
/// TotalMembers spans active and inactive alike. Active / ExpiringSoon /
/// Expired count only members whose own status is active — deactivating a
/// member no longer cancels their membership, so without that clause a
/// deactivated member with an unexpired membership would be counted in the
/// "Active" card while sitting in the Inactive list.
/// </summary>
public record MemberStatsResponse(
    int TotalMembers,
    int Active,
    int ExpiringSoon,
    int Expired,
    int PendingPayments,
    decimal PendingPaymentsValue,
    int Inactive);

/// <summary>
/// Body of PATCH /api/members/{id}/status. Deactivating is reversible and
/// touches nothing but the status column, which is why it is not a DELETE.
/// </summary>
public class MemberStatusRequest : IValidatableObject
{
    [Required]
    public string Status { get; set; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (!MemberEnums.Statuses.Contains(Status))
            yield return new($"Status must be one of: {string.Join(", ", MemberEnums.Statuses)}.", [nameof(Status)]);
    }
}

public record PlanOption(
    long Id,
    string Name,
    int DurationValue,
    string DurationUnit,
    decimal Price);

public class MemberQuery
{
    [StringLength(80)]
    public string? Search { get; set; }

    /// <summary>A membership state, not the member's own status. See MemberEnums.</summary>
    public string? Status { get; set; }

    /// <summary>
    /// The member's own status (active / inactive). Deliberately a separate
    /// parameter from <see cref="Status"/> above: the two vocabularies answer
    /// different questions and a screen filters on both at once.
    /// </summary>
    public string? MemberStatus { get; set; }

    public long? PlanId { get; set; }

    /// <summary>
    /// paid | partial | pending, plus the union value `owing` (= partial or
    /// pending) that the pending-payments screen asks for. `owing` is not a DB
    /// vocabulary word, which is why it is not in MemberEnums.PaymentStates.
    /// </summary>
    public string? PaymentStatus { get; set; }

    /// <summary>`this_month`, in the branch's calendar. See MemberEnums.JoinedWindows.</summary>
    public string? Joined { get; set; }

    /// <summary>
    /// name (default) | balance_desc. Unset behaves exactly as the list always
    /// has, so no existing caller changes.
    /// </summary>
    public string? Sort { get; set; }

    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    /// <summary>
    /// Hard ceiling. Without one, ?pageSize=1000000 is a bulk export of personal
    /// data and a denial of service in the same request.
    /// </summary>
    [Range(1, 100)]
    public int PageSize { get; set; } = 20;
}
