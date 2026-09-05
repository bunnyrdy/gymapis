namespace GymApis.Models.Messaging;

/// <summary>
/// The vocabularies the DB CHECK constraints allow, in one place so a typo is a
/// compile error rather than a 23514 at runtime. Same role
/// AttendanceEnums.StoredStatuses plays for the register.
/// </summary>
public static class MessageChannels
{
    public const string Email = "email";
    public const string Sms   = "sms";
}

public static class MessagePriorities
{
    /// <summary>Password reset and the change notice. Spends from the reserve.</summary>
    public const string High = "high";

    /// <summary>Renewal reminders and offers. Capped at (limit - reserve).</summary>
    public const string Normal = "normal";
}

public static class MessageStatuses
{
    public const string Pending = "pending";
    public const string Sending = "sending";
    public const string Sent    = "sent";
    public const string Failed  = "failed";
    public const string Skipped = "skipped";
}

public static class MessagePurposes
{
    public const string PasswordReset    = "password_reset";
    public const string PasswordChanged  = "password_changed";
    public const string RenewalReminder  = "renewal_reminder";
    public const string Offer            = "offer";
    public const string RegistrationWelcome = "registration_welcome";
    public const string RenewalConfirmation = "renewal_confirmation";

    /// <summary>
    /// Priority is a property of the purpose, never a caller's argument — a
    /// caller who could pass 'high' for an offer would spend the reserve that
    /// exists so a locked-out member can still get a reset link at 3pm.
    /// </summary>
    public static string PriorityFor(string purpose) => purpose switch
    {
        PasswordReset or PasswordChanged => MessagePriorities.High,
        RegistrationWelcome => MessagePriorities.Normal,
        RenewalConfirmation => MessagePriorities.Normal,
        _                                => MessagePriorities.Normal,
    };

    /// <summary>
    /// Account mail, addressed to a `users` row. It is exempt from the
    /// active-member rule (staff have no member record) and from the master
    /// kill switch unless the owner explicitly also suspends transactional mail.
    /// </summary>
    public static bool IsTransactional(string purpose) =>
        purpose is PasswordReset or PasswordChanged;

    /// <summary>Marketing under DPDP: gated on members.marketing_opt_in.</summary>
    public static bool IsMarketing(string purpose) => purpose is Offer;

    /// <summary>
    /// How long the message is still worth sending. A reset link must not
    /// outlive its token — AuthService gives that one hour — and a "expires in
    /// 3 days" reminder delivered on the expiry date is worse than silence.
    /// </summary>
    public static TimeSpan LifetimeFor(string purpose) => purpose switch
    {
        PasswordReset   => TimeSpan.FromHours(1),
        RegistrationWelcome => TimeSpan.FromDays(3),
        RenewalConfirmation => TimeSpan.FromDays(3),
        PasswordChanged => TimeSpan.FromHours(24),
        RenewalReminder => TimeSpan.FromDays(2),
        
        _               => TimeSpan.FromDays(2),
    };
}

public static class MessageSkipReasons
{
    public const string Expired        = "expired";
    public const string MemberInactive = "member_inactive";
    public const string NoConsent      = "no_consent";
    public const string NoAddress      = "no_address";
    public const string Disabled       = "disabled";
}
