using GymApis.Models.Messaging;

namespace GymApis.Services.Messaging;

/// <summary>One rendered message: what goes on the wire, in both parts.</summary>
/// <param name="Subject">NULL only for SMS, which has none.</param>
/// <param name="Html">The HTML part, already wrapped in the mail shell.</param>
/// <param name="Text">
/// The plain-text alternative. Not optional in practice: an HTML-only message
/// scores worse for spam at every major provider, which for a password reset is
/// the difference between a working flow and a broken one.
/// </param>
public readonly record struct RenderedMessage(string? Subject, string Html, string Text);

/// <summary>
/// Every message this system can send, in one file.
///
/// Bodies used to be inline raw strings at the call site (AuthService built the
/// reset mail itself). They moved here when the queue arrived, because the
/// queue stores a *rendered* body and something has to render it in one place
/// — otherwise the same message reads differently depending on which code path
/// queued it.
///
/// No templating engine on purpose. Four messages do not justify Razor or
/// Fluid, and a dependency added "for later" is one more thing to keep patched.
/// Revisit at roughly eight.
///
/// The shell used to live inside EmailSender, which meant LogEmailSender logged
/// something subtly different from what Brevo received — exactly the kind of
/// gap that hides a rendering bug until it is in front of a member.
/// </summary>
public static class MessageTemplates
{
    private const string GymName = "Steel Flex";

    public static RenderedMessage PasswordReset(string link) => Wrap(
        $"Reset your {GymName} password",
        html: $"""
              <p>We received a request to reset your {GymName} password.</p>
              <p><a href="{link}">Reset password</a></p>
              <p>This link expires in one hour. If you didn't ask for it, ignore this email — nothing has changed.</p>
              """,
        text: $"""
              We received a request to reset your {GymName} password.

              Reset password: {link}

              This link expires in one hour. If you didn't ask for it, ignore this
              email — nothing has changed.
              """);

    /// <summary>
    /// Sent after a successful reset. Carries no link and no token — this is
    /// the notice that lets someone spot an account takeover, so it must be
    /// safe to send to an address an attacker may also be reading.
    /// </summary>
    public static RenderedMessage PasswordChanged() => Wrap(
        $"Your {GymName} password was changed",
        html: $"""
              <p>Your {GymName} password was just changed, and every device has been signed out.</p>
              <p>If that was you, nothing more to do.</p>
              <p><strong>If it wasn't you, contact the gym straight away.</strong></p>
              """,
        text: $"""
              Your {GymName} password was just changed, and every device has been
              signed out.

              If that was you, nothing more to do.

              If it wasn't you, contact the gym straight away.
              """);

    public static RenderedMessage RenewalReminder(
        string memberName, string planName, DateOnly endsOn, int daysRemaining) => Wrap(
        $"Your {GymName} membership expires in {Days(daysRemaining)}",
        html: $"""
              <p>Hi {memberName},</p>
              <p>Your <strong>{planName}</strong> membership ends on <strong>{endsOn:d MMMM yyyy}</strong> — that's {Days(daysRemaining)} away.</p>
              <p>Drop by the front desk or give us a call to renew and keep your streak going.</p>
              <p>See you at the gym.</p>
              """,
        text: $"""
              Hi {memberName},

              Your {planName} membership ends on {endsOn:d MMMM yyyy} — that's
              {Days(daysRemaining)} away.

              Drop by the front desk or give us a call to renew and keep your
              streak going.

              See you at the gym.
              """);

    private static string Days(int n) => n == 1 ? "1 day" : $"{n} days";

    /// <summary>
    /// The mail shell. Inline styles only — every mail client strips a
    /// stylesheet, and half of them strip a &lt;style&gt; block too.
    /// </summary>
    private static RenderedMessage Wrap(string subject, string html, string text) => new(
        subject,
        $"""<div style="font-family:sans-serif;font-size:16px;line-height:1.5">{html}</div>""",
        text.ReplaceLineEndings("\n"));
}
