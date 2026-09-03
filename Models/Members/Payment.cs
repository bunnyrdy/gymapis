namespace GymApis.Models.Members;

/// <summary>
/// Maps onto `payments` — one row per transaction, NOT per membership.
///
/// The Add Member screen shows Price / Paid / Remaining, but only the first is
/// stored (on `memberships`). Paid and Remaining are always derived by summing
/// completed payments, so the two can never drift apart, and partial payments
/// and installments work with no extra design.
///
/// The column has CHECK (amount > 0): a member who has paid nothing yet has no
/// payment row at all. "Pending" is the absence of a payment, not a zero one.
/// </summary>
public class Payment
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long BranchId { get; set; }

    public string? ReceiptNo { get; set; }

    public long MemberId { get; set; }
    public long? MembershipId { get; set; }

    /// <summary>membership | personal_training | registration | merchandise | other.</summary>
    public string Purpose { get; set; } = "membership";

    public decimal Amount { get; set; }

    /// <summary>cash | card | bank_transfer | upi | other (DB CHECK constraint).</summary>
    public string Method { get; set; } = default!;

    public DateTimeOffset PaidAt { get; set; }

    /// <summary>UPI ref / txn id / cheque no. Always NULL for cash.</summary>
    public string? ReferenceNo { get; set; }

    /// <summary>completed | pending | failed | refunded (DB CHECK constraint).</summary>
    public string Status { get; set; } = "completed";

    public long? CollectedByUserId { get; set; }
    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
