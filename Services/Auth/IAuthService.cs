using GymApis.Dtos;

namespace GymApis.Services.Auth;

public interface IAuthService
{
    Task<AuthResult> LoginAsync(LoginRequest req, ClientInfo client, CancellationToken ct);
    Task<AuthResult> RefreshAsync(RefreshRequest req, ClientInfo client, CancellationToken ct);
    Task LogoutAsync(LogoutRequest req, CancellationToken ct);
    Task ForgotPasswordAsync(ForgotPasswordRequest req, CancellationToken ct);
    Task<AuthResult> ResetPasswordAsync(ResetPasswordRequest req, CancellationToken ct);
}
