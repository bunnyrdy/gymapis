using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using GymApis.Models.Auth;
using GymApis.Models.Jwt;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GymApis.Services.Auth;

public class TokenService : ITokenService
{
    private readonly JwtOptions _jwt;

    public TokenService(IOptions<JwtOptions> jwt) => _jwt = jwt.Value;

    public string GenerateAccessToken(User user)
    {
        // Claims per 002_refresh_tokens.sql: sub, tenant_id, branch_id, role, exp, jti.
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub,   user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti,   Guid.NewGuid().ToString()),
            new Claim("tenant_id", user.TenantId.ToString()),
            new Claim("branch_id", Tenancy.BranchId.ToString()),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("role", user.Role),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SigningKey));
        var token = new JwtSecurityToken(
            issuer:             _jwt.Issuer,
            audience:           _jwt.Audience,
            claims:             claims,
            expires:            DateTime.UtcNow.AddMinutes(_jwt.AccessTokenMinutes),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public (string Raw, string Hash) GenerateOpaqueToken()
    {
        // 256 bits of CSPRNG. Url-safe so it can also ride in a reset link.
        var bytes = RandomNumberGenerator.GetBytes(32);
        var raw   = Base64UrlEncoder.Encode(bytes);
        return (raw, Hash(raw));
    }

    // SHA-256, deliberately NOT bcrypt: the input is already 256 bits of
    // randomness, so there is nothing to brute-force, and bcrypt would add
    // ~100ms to every single refresh call. (Reasoning copied from
    // 002_refresh_tokens.sql — keep the two in agreement.)
    public string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
}
