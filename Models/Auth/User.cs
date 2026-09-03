namespace GymApis.Models.Auth;

/// <summary>
/// Maps onto the `users` table from schema_v1.sql. Identity + credentials only;
/// the human details (name, phone, photo) live on `staff` / `members`.
/// </summary>
public class User
{
    public long Id { get; set; }
    public long TenantId { get; set; }

    /// <summary>citext in Postgres — comparisons are already case-insensitive.</summary>
    public string Email { get; set; } = default!;

    public string PasswordHash { get; set; } = default!;

    /// <summary>owner | admin | manager | trainer | receptionist | member (DB CHECK constraint).</summary>
    public string Role { get; set; } = default!;

    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>SHA-256 of the emailed reset token. The raw value is never stored.</summary>
    public string? ResetTokenHash { get; set; }
    public DateTimeOffset? ResetExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
