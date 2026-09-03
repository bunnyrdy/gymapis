using System.ComponentModel.DataAnnotations;

namespace GymApis.Dtos;

/// <summary>
/// The Messaging tab of the Settings screen: what the owner may change, plus
/// where the day's allowance currently stands.
/// </summary>
/// <param name="QuotaResetsAtLocal">
/// When the vendor's counter rolls over, rendered in the branch's own clock.
/// The quota day is UTC — Brevo's, not the gym's — so on Asia/Kolkata this
/// reads 5:30 AM, and a screen that showed midnight would have the owner
/// expecting headroom five and a half hours before it arrives.
/// </param>
public record MessagingSettingsResponse(
    bool EmailsEnabled,
    bool SuspendTransactional,
    int DailyEmailLimit,
    int HighPriorityReserve,
    IReadOnlyList<int> ReminderDaysBefore,
    int HighSentToday,
    int NormalSentToday,
    int NormalRemainingToday,
    int PendingCount,
    int FailedCount,
    DateTimeOffset QuotaResetsAtUtc,
    DateTime QuotaResetsAtLocal);

/// <summary>
/// The write allow-list. Note what is absent: TenantId, the counters, and
/// anything about a specific message. Settings are settings.
/// </summary>
public class MessagingSettingsWriteRequest : IValidatableObject
{
    public bool EmailsEnabled { get; set; } = true;

    /// <summary>
    /// Also stop password-reset mail. Its own field rather than folded into
    /// <see cref="EmailsEnabled"/>, because the two decisions have very
    /// different consequences and only one of them can lock the owner out of
    /// their own account.
    /// </summary>
    public bool SuspendTransactional { get; set; }

    [Range(1, 100_000)]
    public int DailyEmailLimit { get; set; } = 270;

    [Range(0, 100_000)]
    public int HighPriorityReserve { get; set; } = 30;

    /// <summary>e.g. [3] or [7, 3, 1]. Empty disables renewal reminders.</summary>
    public int[] ReminderDaysBefore { get; set; } = [3];

    /// <summary>
    /// Mirrors the DB CHECKs rather than trusting them: a constraint violation
    /// is a 500 with a Postgres message in the log, a validation failure is a
    /// 400 the form can render next to the field.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (HighPriorityReserve >= DailyEmailLimit)
            yield return new ValidationResult(
                "The reserve must be smaller than the daily limit, or no ordinary mail could ever be sent.",
                [nameof(HighPriorityReserve)]);

        if (ReminderDaysBefore.Length > 5)
            yield return new ValidationResult(
                "At most five reminders per membership.", [nameof(ReminderDaysBefore)]);

        if (ReminderDaysBefore.Any(d => d is < 0 or > 90))
            yield return new ValidationResult(
                "Each reminder must be between 0 and 90 days before expiry.", [nameof(ReminderDaysBefore)]);

        if (ReminderDaysBefore.Length != ReminderDaysBefore.Distinct().Count())
            yield return new ValidationResult(
                "The same reminder day is listed twice.", [nameof(ReminderDaysBefore)]);
    }
}

/// <summary>
/// One row of the queue, as the Settings screen sees it.
///
/// THERE IS NO BODY HERE AND THERE MUST NOT BE. A pending password-reset row
/// holds a live link; the whole reason bodies are cleared on delivery is that
/// nothing should be able to read one back out. A screen that rendered the body
/// would hand any admin a working reset link for any account.
/// </summary>
public record QueuedMessageResponse(
    long Id,
    string Purpose,
    string Priority,
    string Status,
    string? SkipReason,
    string ToAddress,
    string? Subject,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset ExpiresAt);
