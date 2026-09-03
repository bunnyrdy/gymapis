using GymApis.Models.Messaging;
using GymApis.Repos;
using Microsoft.EntityFrameworkCore;

namespace GymApis.Services.Messaging;

/// <summary>
/// The answer to "may we send this?", and the reason it is null.
/// </summary>
/// <param name="Allowed">False means do not send; <paramref name="SkipReason"/> says why.</param>
/// <param name="SkipReason">One of <see cref="MessageSkipReasons"/>, or null when allowed.</param>
public readonly record struct SendVerdict(bool Allowed, string? SkipReason)
{
    public static SendVerdict Allow() => new(true, null);
    public static SendVerdict Skip(string reason) => new(false, reason);
}

public interface IMessagingPolicy
{
    /// <summary>
    /// Whether this addressee may receive this purpose right now. Called twice
    /// per message and deliberately so — see the class comment.
    /// </summary>
    Task<SendVerdict> MayReceiveAsync(string purpose, long? memberId, long? userId, CancellationToken ct);
}

/// <summary>
/// The one place the "who may be messaged" rules are written.
///
/// It is consulted TWICE for every message, at enqueue and again at send, and
/// that is not redundancy — the two calls answer different questions:
///
///   * at enqueue: "is there any point queuing this?" — an optimisation.
///   * at send:    "is this still true?"              — the authoritative one.
///
/// A reminder queued on Monday and sent on Wednesday to somebody deactivated on
/// Tuesday is precisely the failure this rule exists to prevent, and only the
/// send-time call catches it. Both call the same method, so the rule itself
/// still lives in exactly one place.
///
/// THE EXEMPTION IS NOT OPTIONAL. Password-reset mail is addressed to a `users`
/// row, not a `members` row: owners, admins, receptionists and trainers have no
/// member record at all. Applying the active-member filter to everything —
/// which looks like a tidy-up — locks the owner out of their own system with no
/// way back in. That is why the rule keys off `purpose` rather than off whether
/// a member id happens to be present.
/// </summary>
public class MessagingPolicy : IMessagingPolicy
{
    private readonly GymDbContext _db;

    public MessagingPolicy(GymDbContext db) => _db = db;

    public async Task<SendVerdict> MayReceiveAsync(
        string purpose, long? memberId, long? userId, CancellationToken ct)
    {
        // Account mail. The member rules cannot apply and must not: see above.
        // A deactivated *user* is already handled upstream — AuthService returns
        // early for `!user.IsActive`, the same way login does.
        if (MessagePurposes.IsTransactional(purpose))
            return userId is null ? SendVerdict.Skip(MessageSkipReasons.NoAddress) : SendVerdict.Allow();

        if (memberId is null)
            return SendVerdict.Skip(MessageSkipReasons.NoAddress);

        // IgnoreQueryFilters so an erased member resolves rather than vanishing:
        // "erased" must produce a definite no, not a silent nothing-found that
        // reads the same as a bad id.
        var member = await _db.Members
            .IgnoreQueryFilters()
            .Where(m => m.Id == memberId && m.TenantId == Tenancy.TenantId)
            .Select(m => new { m.Status, m.DeletedAt, m.Email, m.MarketingOptIn })
            .FirstOrDefaultAsync(ct);

        if (member is null || member.DeletedAt is not null)
            return SendVerdict.Skip(MessageSkipReasons.MemberInactive);

        // The contract the rest of the codebase already writes down: a member
        // with status <> 'active' receives nothing. Deactivating is reversible
        // and does not cancel their membership, so a lapsed reminder would
        // otherwise still chase somebody the gym has switched off.
        if (member.Status != "active")
            return SendVerdict.Skip(MessageSkipReasons.MemberInactive);

        // members.email is nullable — a walk-in signed up with a phone number
        // and nothing else has no address to mail.
        if (string.IsNullOrWhiteSpace(member.Email))
            return SendVerdict.Skip(MessageSkipReasons.NoAddress);

        // DPDP. Only marketing consults consent; a renewal reminder is a service
        // message about a contract they signed. Which is exactly why a renewal
        // reminder must never carry an offer in its body.
        if (MessagePurposes.IsMarketing(purpose) && !member.MarketingOptIn)
            return SendVerdict.Skip(MessageSkipReasons.NoConsent);

        return SendVerdict.Allow();
    }
}
