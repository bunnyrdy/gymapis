using GymApis.Dtos;
using GymApis.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using GymApis.Services;

namespace GymApis.Controllers.Auth;

/// <summary>
/// HTTP surface for authentication. Deliberately thin: pulls request metadata,
/// delegates to AuthService, maps the result to a status code. No business
/// logic lives here.
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;

    public AuthController(IAuthService auth) => _auth = auth;

    // Rate limiting is applied per action, not on the controller. login,
    // forgot-password and reset-password are the guessable/expensive ones;
    // refresh and logout must stay unlimited or the SPA's 401 interceptor signs
    // a busy front desk out mid-shift. See RateLimitPolicies.Auth.
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await _auth.LoginAsync(request, GetClientInfo(), ct);
        return result.Succeeded
            ? Ok(result.Response)
            : Unauthorized(new { errors = result.Errors });
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken ct)
    {
        var result = await _auth.RefreshAsync(request, GetClientInfo(), ct);
        return result.Succeeded
            ? Ok(result.Response)
            : Unauthorized(new { errors = result.Errors });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken ct)
    {
        await _auth.LogoutAsync(request, ct);
        return NoContent();
    }

    /// <summary>
    /// Always 202, even for an address with no account — otherwise this becomes
    /// an account-enumeration oracle.
    /// </summary>
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct)
    {
        await _auth.ForgotPasswordAsync(request, ct);
        return Accepted(new { message = "If that email has an account, a reset link is on its way." });
    }

    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken ct)
    {
        var result = await _auth.ResetPasswordAsync(request, ct);
        return result.Succeeded
            ? Ok(new { message = "Password updated. Please sign in." })
            : BadRequest(new { errors = result.Errors });
    }

    private ClientInfo GetClientInfo() => new(
        HttpContext.Connection.RemoteIpAddress,
        Request.Headers.UserAgent.ToString());
}
