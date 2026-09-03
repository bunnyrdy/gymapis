using GymApis.Models.Messaging;
using GymApis.Repos;

namespace GymApis.Services.Messaging;

public interface IMessageQueue
{
    /// <summary>
    /// Queues a message. Adds to the change tracker WITHOUT saving — the
    /// caller's own SaveChangesAsync commits it, see the class comment.
    /// Returns the row when it was queued, or null when policy said no.
    /// </summary>
    Task<QueuedMessage?> EnqueueAsync(
        string purpose,
        string toAddress,
        RenderedMessage body,
        long? memberId = null,
        long? userId = null,
        string? dedupeKey = null,
        CancellationToken ct = default);
}

/// <summary>
/// The only way anything gets sent.
///
/// ENQUEUE DOES NOT SAVE, and that is the point. It adds the row to the
/// caller's change tracker so the caller's existing SaveChangesAsync commits
/// the domain change and the message together, in one transaction. A password
/// reset token therefore cannot exist without its email queued, and an email
/// cannot be queued for a token that was never issued — the two states that a
/// send-then-save or save-then-send ordering would let you reach.
///
/// It is also why nothing here talks to Brevo. The request thread does a
/// database insert and returns; the provider call happens on the dispatch
/// worker. That is what makes the forgot-password 202 *structurally* uniform:
/// there is no longer an outbound call in that request that could fail
/// differently for a real address than for an unknown one, which is the
/// account-enumeration oracle the uniform response exists to close.
/// </summary>
public class MessageQueue : IMessageQueue
{
    private readonly GymDbContext _db;
    private readonly IMessagingPolicy _policy;
    private readonly ILogger<MessageQueue> _log;

    public MessageQueue(GymDbContext db, IMessagingPolicy policy, ILogger<MessageQueue> log)
    {
        _db = db;
        _policy = policy;
        _log = log;
    }

    public async Task<QueuedMessage?> EnqueueAsync(
        string purpose,
        string toAddress,
        RenderedMessage body,
        long? memberId = null,
        long? userId = null,
        string? dedupeKey = null,
        CancellationToken ct = default)
    {
        // The optimisation half of the two-call rule in MessagingPolicy: no
        // point storing a row the dispatcher would only skip. The authoritative
        // check is the dispatcher's, because the answer can change in between.
        var verdict = await _policy.MayReceiveAsync(purpose, memberId, userId, ct);
        if (!verdict.Allowed)
        {
            _log.LogInformation(
                "Not queuing {Purpose} for member {MemberId}/user {UserId}: {Reason}.",
                purpose, memberId, userId, verdict.SkipReason);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var message = new QueuedMessage
        {
            TenantId  = Tenancy.TenantId,
            // Account mail belongs to no branch; member mail does.
            BranchId  = memberId is null ? null : Tenancy.BranchId,
            Channel   = MessageChannels.Email,
            Priority  = MessagePurposes.PriorityFor(purpose),
            Purpose   = purpose,
            MemberId  = memberId,
            UserId    = userId,
            ToAddress = toAddress,
            Subject   = body.Subject,
            BodyHtml  = body.Html,
            BodyText  = body.Text,
            DedupeKey = dedupeKey,
            Status    = MessageStatuses.Pending,
            NextAttemptAt = now,
            ExpiresAt = now + MessagePurposes.LifetimeFor(purpose),
        };

        _db.MessageQueue.Add(message);
        return message;
    }
}
