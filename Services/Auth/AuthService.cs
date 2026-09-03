using GymApis.Dtos;
using GymApis.Models.Auth;
using GymApis.Models.Jwt;
using GymApis.Repos;
using GymApis.Models.Messaging;
using GymApis.Services.Messaging;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GymApis.Services.Auth;

public class AuthService : IAuthService
{
    private const string InvalidCredentials = "Invalid email or password.";

    private readonly GymDbContext        _db;
    private readonly ITokenService       _tokens;
    private readonly IPasswordHasher<User> _hasher;
    private readonly IMessageQueue       _queue;
    private readonly JwtOptions          _jwt;
    private readonly string              _frontendBaseUrl;
    private readonly ILogger<AuthService> _log;

    public AuthService(
        GymDbContext db,
        ITokenService tokens,
        IPasswordHasher<User> hasher,
        IMessageQueue queue,
        IOptions<JwtOptions> jwt,
        IConfiguration config,
        ILogger<AuthService> log)
    {
        _db     = db;
        _tokens = tokens;
        _hasher = hasher;
        _queue  = queue;
        _jwt    = jwt.Value;
        _log    = log;
        _frontendBaseUrl = config["Frontend:BaseUrl"] ?? "http://localhost:5173";
    }

    // -----------------------------------------------------------------------
    // LOGIN
    // -----------------------------------------------------------------------
    public async Task<AuthResult> LoginAsync(LoginRequest req, ClientInfo client, CancellationToken ct)
    {
        // Email is citext, so this comparison is case-insensitive in the DB.
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.TenantId == Tenancy.TenantId && u.Email == req.Email, ct);

        // Identical message on "no such user" and "wrong password" — no user enumeration.
        if (user is null)
            return AuthResult.Fail(InvalidCredentials);

        var verify = _hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
        if (verify == PasswordVerificationResult.Failed)
            return AuthResult.Fail(InvalidCredentials);

        // A deactivated account gets the same vague message; the account's
        // existence is not something an anonymous caller should be able to probe.
        if (!user.IsActive)
            return AuthResult.Fail(InvalidCredentials);

        if (verify == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = _hasher.HashPassword(user, req.Password);

        // ponytail: no lockout — the `users` table has no failed-attempt columns.
        // Add them plus a counter here if credential stuffing shows up in the logs.

        var (raw, entity) = NewRefreshToken(user.Id, familyId: null, client);
        _db.RefreshTokens.Add(entity);
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return AuthResult.Success(BuildResponse(user, raw));
    }

    // -----------------------------------------------------------------------
    // REFRESH — rotation + reuse detection, per 002_refresh_tokens.sql
    // -----------------------------------------------------------------------
    public async Task<AuthResult> RefreshAsync(RefreshRequest req, ClientInfo client, CancellationToken ct)
    {
        var hash = _tokens.Hash(req.RefreshToken);

        // One transaction: the revoke-old / insert-new pair must not half-apply.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var row = await _db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (row is null)
            return AuthResult.Fail("Invalid refresh token.");

        // REUSE DETECTED: an already-revoked token was presented. A legitimate
        // client discards its old token the moment it rotates, so it can never
        // present one — which means this was copied. Kill the whole family.
        // That turns a stolen refresh token from "weeks of quiet access" into
        // "locked out as soon as either party refreshes".
        if (row.RevokedAt is not null)
        {
            await _db.RefreshTokens
                .Where(t => t.FamilyId == row.FamilyId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow)
                    .SetProperty(t => t.RevokedReason, "reuse_detected"), ct);
            await tx.CommitAsync(ct);

            _log.LogWarning("Refresh token reuse detected for user {UserId}; family {Family} revoked.",
                row.UserId, row.FamilyId);
            return AuthResult.Fail("Invalid refresh token.");
        }

        if (row.ExpiresAt <= DateTimeOffset.UtcNow)
            return AuthResult.Fail("Refresh token expired.");

        if (row.User is null || !row.User.IsActive)
            return AuthResult.Fail("Account is not active.");

        // Happy path: issue a new token in the SAME family, retire the old one.
        var (raw, replacement) = NewRefreshToken(row.UserId, row.FamilyId, client);
        _db.RefreshTokens.Add(replacement);
        await _db.SaveChangesAsync(ct);          // gives `replacement` its id

        row.RevokedAt     = DateTimeOffset.UtcNow;
        row.RevokedReason = "rotated";
        row.ReplacedById  = replacement.Id;
        row.LastUsedAt    = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);
        return AuthResult.Success(BuildResponse(row.User, raw));
    }

    // -----------------------------------------------------------------------
    // LOGOUT
    // -----------------------------------------------------------------------
    public async Task LogoutAsync(LogoutRequest req, CancellationToken ct)
    {
        // Silent no-op when the token is unknown or already dead — the caller
        // learns nothing either way, and logout must never fail loudly.
        var hash = _tokens.Hash(req.RefreshToken);
        await _db.RefreshTokens
            .Where(t => t.TokenHash == hash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, DateTimeOffset.UtcNow)
                .SetProperty(t => t.RevokedReason, "logout"), ct);
    }

    // -----------------------------------------------------------------------
    // FORGOT PASSWORD
    // -----------------------------------------------------------------------
    public async Task ForgotPasswordAsync(ForgotPasswordRequest req, CancellationToken ct)
    {
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.TenantId == Tenancy.TenantId && u.Email == req.Email, ct);

        // Unknown address: do nothing, but the controller still returns 200 —
        // otherwise this endpoint becomes an account-enumeration oracle.
        if (user is null || !user.IsActive)
            return;

        var (raw, hash) = _tokens.GenerateOpaqueToken();
        user.ResetTokenHash = hash;
        user.ResetExpiresAt = DateTimeOffset.UtcNow.AddHours(1);

        var link = $"{_frontendBaseUrl}/reset-password?token={Uri.EscapeDataString(raw)}";

        // Queued, not sent. This used to be an inline Brevo call wrapped in a
        // catch that swallowed every failure, because a 502 for real addresses
        // beside a 202 for unknown ones is the exact enumeration oracle the
        // uniform 202 exists to close. The swallow was right and it was also
        // the whole problem: with a live key behind it, a provider blip
        // silently dropped the message while the caller was told one was coming.
        //
        // With the queue there is nothing left in this request that talks to a
        // provider, so the uniform 202 is now structural rather than defended —
        // and the enqueue joins the SaveChangesAsync below, so a reset token
        // cannot exist without its email queued, or the reverse.
        await _queue.EnqueueAsync(
            MessagePurposes.PasswordReset,
            user.Email,
            MessageTemplates.PasswordReset(link),
            userId: user.Id,
            ct: ct);

        await _db.SaveChangesAsync(ct);
    }

    // -----------------------------------------------------------------------
    // RESET PASSWORD
    // -----------------------------------------------------------------------
    public async Task<AuthResult> ResetPasswordAsync(ResetPasswordRequest req, CancellationToken ct)
    {
        var hash = _tokens.Hash(req.Token);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.ResetTokenHash == hash, ct);

        if (user is null || user.ResetExpiresAt is null || user.ResetExpiresAt <= DateTimeOffset.UtcNow)
            return AuthResult.Fail("This reset link is invalid or has expired.");

        user.PasswordHash   = _hasher.HashPassword(user, req.NewPassword);
        user.ResetTokenHash = null;      // single use
        user.ResetExpiresAt = null;

        // Somebody with a stolen link could get this far. The notice is how the
        // account's real owner finds out, so it carries no link and no token —
        // it has to be safe to deliver to an inbox an attacker may also be
        // reading. Queued in the same save as the new password: the change and
        // the warning about the change commit together.
        await _queue.EnqueueAsync(
            MessagePurposes.PasswordChanged,
            user.Email,
            MessageTemplates.PasswordChanged(),
            userId: user.Id,
            ct: ct);

        await _db.SaveChangesAsync(ct);

        // Changing a password logs every device out. The helper is already
        // defined in 002_refresh_tokens.sql — reuse it rather than re-expressing
        // the same UPDATE in LINQ.
        await _db.Database.ExecuteSqlAsync(
            $"SELECT revoke_user_tokens({user.Id}, 'password_change')", ct);

        return AuthResult.Ok();
    }

    // -----------------------------------------------------------------------
    private (string Raw, RefreshToken Entity) NewRefreshToken(long userId, Guid? familyId, ClientInfo client)
    {
        var (raw, hash) = _tokens.GenerateOpaqueToken();
        var entity = new RefreshToken
        {
            TenantId  = Tenancy.TenantId,
            UserId    = userId,
            TokenHash = hash,
            FamilyId  = familyId ?? Guid.NewGuid(),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(_jwt.RefreshTokenDays),
            IpAddress = client.IpAddress,
            UserAgent = client.UserAgent,
        };
        return (raw, entity);
    }

    private AuthResponse BuildResponse(User user, string rawRefresh) => new(
        AccessToken:      _tokens.GenerateAccessToken(user),
        RefreshToken:     rawRefresh,
        ExpiresInSeconds: _jwt.AccessTokenMinutes * 60,
        UserId:           user.Id,
        Email:            user.Email,
        Role:             user.Role);
}
