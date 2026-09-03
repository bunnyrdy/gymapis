namespace GymApis.Models.Messaging;

/// <summary>
/// Maps onto `message_queue` (009_messaging.sql) — one row per outbound
/// message, from the moment something decides to send it until it is delivered,
/// abandoned or skipped.
///
/// Three properties of this entity are load-bearing and none of them are
/// obvious from the column list:
///
///  * <see cref="BodyHtml"/> AND <see cref="BodyText"/> ARE NULL ONCE TERMINAL.
///    A rendered password-reset email contains the raw reset token, which
///    `users.reset_token_hash` deliberately never stores. Clearing the body on
///    delivery is what keeps that true; a DB CHECK enforces it, so a code path
///    that forgets fails loudly rather than quietly leaving a live link in the
///    database. Never add a "resend" that re-uses a stored body.
///  * EXACTLY ONE OF <see cref="MemberId"/> / <see cref="UserId"/> IS SET.
///    That is what lets MessagingPolicy know which rule applies without
///    guessing: member-directed mail obeys the active-member rule, and
///    user-directed mail cannot, because staff have no member row at all.
///  * <see cref="Priority"/> IS DERIVED FROM <see cref="Purpose"/>, never
///    supplied by a caller. A caller who could ask for 'high' could spend the
///    password-reset reserve.
/// </summary>
public class QueuedMessage
{
    public long Id { get; set; }
    public long TenantId { get; set; }

    /// <summary>NULL for account mail — a password reset belongs to no branch.</summary>
    public long? BranchId { get; set; }

    /// <summary>email | sms. The SMS seam; every V1 row is 'email'.</summary>
    public string Channel { get; set; } = MessageChannels.Email;

    /// <summary>high | normal. Set from <see cref="Purpose"/> by MessageQueue.</summary>
    public string Priority { get; set; } = default!;

    /// <summary>password_reset | password_changed | renewal_reminder | offer.</summary>
    public string Purpose { get; set; } = default!;

    public long? MemberId { get; set; }
    public long? UserId { get; set; }

    /// <summary>Snapshot at enqueue; re-read from the live row before sending.</summary>
    public string ToAddress { get; set; } = default!;

    public string? Subject { get; set; }
    public string? BodyHtml { get; set; }
    public string? BodyText { get; set; }

    /// <summary>
    /// 'renewal:{membershipId}:{daysBefore}' for generated mail, NULL for
    /// anything a person can legitimately ask for twice. A partial unique index
    /// covers the non-null values, which is what makes the reminder generator
    /// safe to run hourly on two instances forever.
    /// </summary>
    public string? DedupeKey { get; set; }

    /// <summary>pending | sending | sent | failed | skipped.</summary>
    public string Status { get; set; } = MessageStatuses.Pending;

    /// <summary>Why a message was never sent. NULL unless Status is 'skipped'.</summary>
    public string? SkipReason { get; set; }

    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>
    /// Past this instant the message is skipped rather than sent. A reset link
    /// that outlives its token is worse than no email; a renewal reminder that
    /// lands the day the membership expired is noise.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Truncated. Never the provider's whole response body.</summary>
    public string? LastError { get; set; }

    /// <summary>Brevo's message id, kept for support tickets.</summary>
    public string? ProviderMessageId { get; set; }

    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Maps onto `message_quota_usage` — how much of the vendor's daily allowance
/// has been spent.
///
/// <see cref="QuotaDate"/> IS A UTC DATE, and that is the one thing to not
/// "fix". Everywhere else in this codebase "today" means the branch's day
/// (Asia/Kolkata, via IBranchClock) because the thing being counted is a local
/// fact. Here the thing being counted is Brevo's allowance, and Brevo resets at
/// UTC midnight. Counting in IST would reset our counter 5.5 hours early, into
/// a vendor bucket that already holds a full day of sends.
/// </summary>
public class MessageQuotaUsage
{
    public DateOnly QuotaDate { get; set; }
    public string Channel { get; set; } = MessageChannels.Email;
    public int HighSent { get; set; }
    public int NormalSent { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
