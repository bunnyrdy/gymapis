using System.Net;

namespace GymApis.Dtos;

public record LoginRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);
public record LogoutRequest(string RefreshToken);
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Token, string NewPassword);

/// <summary>
/// What the client stores. The access token is a JWT carrying tenant_id,
/// branch_id and role; the refresh token is an opaque random string.
/// </summary>
public record AuthResponse(
    string AccessToken,
    string RefreshToken,
    int    ExpiresInSeconds,
    long   UserId,
    string Email,
    string Role);

/// <summary>Request metadata the controller reads off HttpContext for the token row.</summary>
public record ClientInfo(IPAddress? IpAddress, string? UserAgent);

/// <summary>
/// Result envelope so services never throw for expected failures (bad password,
/// expired token) — the controller maps Succeeded onto a status code.
/// </summary>
public class AuthResult
{
    public bool Succeeded { get; private init; }
    public AuthResponse? Response { get; private init; }
    public IEnumerable<string> Errors { get; private init; } = [];

    public static AuthResult Success(AuthResponse response) =>
        new() { Succeeded = true, Response = response };

    /// <summary>Succeeded, but there is nothing to hand back (e.g. password reset).</summary>
    public static AuthResult Ok() => new() { Succeeded = true };

    public static AuthResult Fail(params string[] errors) =>
        new() { Succeeded = false, Errors = errors };
}
