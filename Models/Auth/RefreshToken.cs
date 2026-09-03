using System.Net;

namespace GymApis.Models.Auth;

/// <summary>
/// Maps onto `refresh_tokens` from 002_refresh_tokens.sql.
/// Access tokens are JWTs and are never stored. This row is the refresh token:
/// 256 bits of CSPRNG randomness, kept as a SHA-256 hash.
/// </summary>
public class RefreshToken
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long UserId { get; set; }

    public string TokenHash { get; set; } = default!;

    /// <summary>One family per login. Rotation keeps the family, changes the token.</summary>
    public Guid FamilyId { get; set; }

    /// <summary>Rotation chain: the token that superseded this one.</summary>
    public long? ReplacedById { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>rotated | logout | logout_all | password_change | reuse_detected | admin_revoked | user_deactivated</summary>
    public string? RevokedReason { get; set; }

    public IPAddress? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public User User { get; set; } = default!;
}
