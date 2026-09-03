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
}
