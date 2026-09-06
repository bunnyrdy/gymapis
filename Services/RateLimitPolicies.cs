namespace GymApis.Services;

/// <summary>
/// Named rate-limiting policies.
///
/// Until the public website there was nothing in this API an anonymous caller
/// could reach except sign-in, and every other endpoint cost an attacker a
/// valid token first. The marketing site changes that: four endpoints, no
/// credentials, several database round trips each. A loop against them is a
/// denial of service that needs no account.
///
/// The limiter partitions on the caller's IP, so one noisy visitor cannot
/// starve the rest, and rejections come back as the same problem+json shape as
/// every other failure — see the 429 branch in Program.cs.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>The anonymous website endpoints.</summary>
    public const string Public = nameof(Public);

    /// <summary>Requests per window, per IP.</summary>
    public const int PublicPermitLimit = 60;

    /// <summary>Window length in seconds.</summary>
    public const int PublicWindowSeconds = 60;

    /// <summary>
    /// How many requests wait rather than fail once the window is full. Small
    /// on purpose: a short queue smooths a page that fires three requests at
    /// once, while a long one just converts a flood into held connections.
    /// </summary>
    public const int PublicQueueLimit = 10;

    /// <summary>
    /// The unauthenticated sign-in surface: login, forgot-password and
    /// reset-password.
    ///
    /// Every one of those is [AllowAnonymous] and every one is expensive to
    /// leave open. Unlimited login is password spraying against a known email
    /// (the gym's own address is on its marketing site). Unlimited
    /// forgot-password is worse than it looks: each request is a queued message
    /// spending the day's Brevo allowance, so a few hundred of them silence the
    /// renewal reminders the gym actually depends on — and the reserve in
    /// claim_message_batch() protects high priority from normal, not from a
    /// flood of high priority.
    ///
    /// Deliberately NOT applied to refresh or logout. The SPA's interceptor
    /// calls refresh on every 401 and access tokens live 15 minutes, so a busy
    /// front desk with several tabs open would trip a limit this tight and be
    /// signed out mid-shift. Those two are applied per action, not on the
    /// controller, for exactly that reason.
    /// </summary>
    public const string Auth = nameof(Auth);

    /// <summary>
    /// Ten attempts per five minutes, per IP. Loose enough that a receptionist
    /// mistyping a password three times in a row never notices; tight enough
    /// that guessing is hopeless.
    /// </summary>
    public const int AuthPermitLimit = 10;

    /// <summary>Window length in seconds.</summary>
    public const int AuthWindowSeconds = 300;

    /// <summary>
    /// No queue. Queueing a rejected sign-in would hold the connection and then
    /// answer it anyway, which is not a limit — it is a delay. A caller over
    /// the ceiling should be told 429 immediately.
    /// </summary>
    public const int AuthQueueLimit = 0;
}
