using GymApis.Models.Auth;

namespace GymApis.Services.Auth;

public interface ITokenService
{
    string GenerateAccessToken(User user);

    /// <summary>Returns the raw token (sent to the client, never stored) and its SHA-256 hash (stored).</summary>
    (string Raw, string Hash) GenerateOpaqueToken();

    /// <summary>SHA-256 of a presented raw token, for looking the stored row up.</summary>
    string Hash(string raw);
}
