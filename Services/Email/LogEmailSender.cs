namespace GymApis.Services.Email;

/// <summary>
/// Development fallback used when Brevo:ApiKey is not configured. Writes the
/// message (reset link included) to the log so the flow is testable without an
/// email account. Never selected when a key is present.
///
/// It runs on the dispatch worker now rather than the request thread, so the
/// link appears a tick after the request rather than during it — and it logs
/// exactly the markup Brevo would have been handed, because MessageTemplates
/// owns the shell rather than EmailSender.
/// </summary>
public sealed class LogEmailSender : IEmailSender
{
    private readonly ILogger<LogEmailSender> _log;

    public LogEmailSender(ILogger<LogEmailSender> log) => _log = log;

    public Task<EmailSendResult> SendAsync(
        string toEmail, string subject, string htmlBody, string? textBody = null,
        CancellationToken ct = default)
    {
        _log.LogWarning("EMAIL (not sent — no Brevo:ApiKey configured)\n  To: {To}\n  Subject: {Subject}\n  {Body}",
            toEmail, subject, textBody ?? htmlBody);

        return Task.FromResult(EmailSendResult.Ok($"log-{Guid.NewGuid():N}"));
    }
}
