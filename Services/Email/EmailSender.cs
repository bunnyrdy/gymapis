using System.Net.Http.Json;
using System.Text.Json;

namespace GymApis.Services.Email;

/// <summary>
/// Brevo transactional API (POST https://api.brevo.com/v3/smtp/email).
/// Auth is an 'api-key' HEADER; 'from' must be a sender verified in the Brevo
/// dashboard. Config lives in user-secrets: Brevo:ApiKey, Brevo:FromEmail,
/// Brevo:FromName.
///
/// Two things changed when the queue arrived:
///
///  * IT NO LONGER THROWS ON A REJECTION. The only caller is now
///    MessageDispatchWorker, which owns the retry ladder and needs the status
///    code to decide between "try again in a minute" and "the day's allowance
///    is gone." An exception would have thrown that information away.
///  * IT SENDS A TEXT PART. An HTML-only message scores worse for spam at every
///    major provider, and for a password reset that is the difference between a
///    working flow and a support call.
///
/// The message shell is NOT applied here any more — MessageTemplates owns it,
/// so LogEmailSender logs byte-for-byte what Brevo would have received.
/// </summary>
public sealed class EmailSender : IEmailSender
{
    /// <summary>
    /// Brevo's own error bodies run to a few hundred characters and land in
    /// `message_queue.last_error`, which a Settings screen renders. Enough to
    /// diagnose, not enough to be a paste of someone else's payload.
    /// </summary>
    private const int MaxErrorLength = 400;

    private readonly HttpClient _http;
    private readonly string _fromEmail;
    private readonly string _fromName;
    private readonly ILogger<EmailSender> _log;

    public EmailSender(HttpClient http, IConfiguration config, ILogger<EmailSender> log)
    {
        _http = http;
        _log  = log;

        var apiKey = config["Brevo:ApiKey"]
            ?? throw new InvalidOperationException("Missing 'Brevo:ApiKey' configuration.");
        _fromEmail = config["Brevo:FromEmail"]
            ?? throw new InvalidOperationException("Missing 'Brevo:FromEmail' (a verified Brevo sender).");
        _fromName = config["Brevo:FromName"] ?? "Steel Flex";

        _http.BaseAddress = new Uri("https://api.brevo.com/");
        _http.DefaultRequestHeaders.Add("api-key", apiKey);
    }

    public async Task<EmailSendResult> SendAsync(
        string toEmail, string subject, string htmlBody, string? textBody = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            sender      = new { name = _fromName, email = _fromEmail },
            to          = new[] { new { email = toEmail } },
            subject,
            htmlContent = htmlBody,
            textContent = textBody,
        };

        using var response = await _http.PostAsJsonAsync("v3/smtp/email", payload, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // The provider's body goes to the log in full and to the row
            // truncated. It names no member and carries no token — it is
            // Brevo's complaint about our request, not our request's contents.
            _log.LogWarning("Brevo rejected a message ({Status}): {Detail}",
                (int)response.StatusCode, body);

            return EmailSendResult.Fail((int)response.StatusCode, Truncate(body));
        }

        return EmailSendResult.Ok(ReadMessageId(body));
    }

    /// <summary>Brevo answers 201 with {"messageId":"&lt;...@smtp-relay.mailin.fr&gt;"}.</summary>
    private static string? ReadMessageId(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("messageId", out var id) ? id.GetString() : null;
        }
        catch (JsonException)
        {
            // A message id is a convenience for support tickets. Failing to
            // parse one must never turn a delivered message into a failed one.
            return null;
        }
    }

    private static string Truncate(string s) =>
        s.Length <= MaxErrorLength ? s : s[..MaxErrorLength];
}
