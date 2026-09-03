using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace GymApis.Services;

public interface ICurrentUser
{
    /// <summary>The signed-in user's id, from the verified `sub` claim. Null when anonymous.</summary>
    long? UserId { get; }
    string? Role { get; }
}

/// <summary>
/// Reads identity off the CURRENT REQUEST'S validated token — never off a
/// request body or header the caller controls. Used for the activity_log actor,
/// so "who did this" cannot be spoofed by the client.
/// </summary>
public class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _http;

    public HttpCurrentUser(IHttpContextAccessor http) => _http = http;

    public long? UserId =>
        long.TryParse(Find(JwtRegisteredClaimNames.Sub) ?? Find(ClaimTypes.NameIdentifier), out var id)
            ? id
            : null;

    public string? Role => Find(ClaimTypes.Role) ?? Find("role");

    private string? Find(string type) =>
        _http.HttpContext?.User.FindFirst(type)?.Value;
}
