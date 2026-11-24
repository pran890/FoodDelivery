using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FoodDelivery.Identity.Features.Tokens;

/// <summary>
/// Writes access tokens. The claims are a contract with every service and frontend:
/// changing one is a schema change (see docs/learning/11-jwt-issuing.md).
/// </summary>
internal sealed class TokenIssuer(SigningKey signingKey, IOptions<JwtOptions> options, TimeProvider time)
{
    private readonly JwtOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new();

    public TimeSpan Lifetime => _options.Lifetime;

    public string Issue(Guid userId, string email, IReadOnlyList<string> roles)
    {
        var now = time.GetUtcNow().UtcDateTime;

        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            // Header: alg and kid come from the credentials. "at+jwt" (RFC 9068) says "access token",
            // so another kind of JWT signed with the same key can't be used in its place.
            TokenType = "at+jwt",
            SigningCredentials = signingKey.Credentials,

            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + _options.Lifetime,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = userId.ToString(),
                [JwtRegisteredClaimNames.Email] = email,
                // An array even for one role, so that clients have a single case to handle.
                ["role"] = roles.ToArray(),
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
            },
        });
    }
}
