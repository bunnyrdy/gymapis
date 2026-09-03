namespace GymApis.Services.Email;

/// <summary>The outcome of one delivery attempt.</summary>
/// <param name="Sent">True when the provider accepted the message.</param>
/// <param name="StatusCode">
/// The provider's HTTP status, when there was one. The dispatcher branches on
/// it: a 429 means the allowance is gone and retrying on the usual ladder would
/// burn five attempts in six hours against a counter that only resets at UTC
/// midnight.
/// </param>
/// <param name="ProviderMessageId">Kept for support tickets. Null on failure.</param>
/// <param name="Error">Short, already-truncated. For the log and `last_error`.</param>
public readonly record struct EmailSendResult(
    bool Sent, int? StatusCode, string? ProviderMessageId, string? Error)
{
    public static EmailSendResult Ok(string? id) => new(true, 200, id, null);
    public static EmailSendResult Fail(int? status, string? error) => new(false, status, null, error);
}

public interface IEmailSender
{
    /// <summary>
    /// Attempts one delivery. A provider rejection is an EXPECTED failure and
    /// comes back in the result — the caller is a background worker with a
    /// retry ladder, not a request that needs a status code. Only a genuine
    /// transport fault (DNS, socket, timeout) throws.
    /// </summary>
    Task<EmailSendResult> SendAsync(
        string toEmail, string subject, string htmlBody, string? textBody = null,
        CancellationToken ct = default);
}
