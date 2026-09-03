namespace GymApis.Models.Jwt;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>From user-secrets, never appsettings.json.</summary>
    public string SigningKey { get; set; } = default!;
    public string Issuer { get; set; } = "gymapis";
    public string Audience { get; set; } = "gymapp";

    /// <summary>Short on purpose — revocation is handled by the refresh token, not this.</summary>
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
}
