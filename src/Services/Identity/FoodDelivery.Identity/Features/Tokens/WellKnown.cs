using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace FoodDelivery.Identity.Features.Tokens;

/// <summary>
/// The two public addresses other services read. The names and shapes are standards (OpenID Connect
/// Discovery and RFC 7517), so any JWT library understands them.
/// </summary>
internal static class WellKnown
{
    private const string JwksPath = "/.well-known/jwks.json";

    internal sealed record Discovery(
        [property: JsonPropertyName("issuer")] string Issuer,
        [property: JsonPropertyName("jwks_uri")] string JwksUri);

    internal sealed record Jwks([property: JsonPropertyName("keys")] IReadOnlyList<PublicKey> Keys);

    /// <summary>Written out field by field, so that a private part of the key can never slip in.</summary>
    internal sealed record PublicKey(
        [property: JsonPropertyName("kty")] string KeyType,
        [property: JsonPropertyName("use")] string Use,
        [property: JsonPropertyName("alg")] string Algorithm,
        [property: JsonPropertyName("kid")] string KeyId,
        [property: JsonPropertyName("n")] string Modulus,
        [property: JsonPropertyName("e")] string Exponent);

    public static void Map(IEndpointRouteBuilder app)
    {
        // Public and not rate limited: our own services read these, often from one shared address.
        var group = app.MapGroup("").AllowAnonymous().DisableRateLimiting();

        group.MapGet("/.well-known/openid-configuration", (HttpContext http, IOptions<JwtOptions> options) =>
        {
            AllowCaching(http);
            // Built from the address the caller used: inside Aspire, and later behind the gateway, it differs.
            return new Discovery(options.Value.Issuer, $"{http.Request.Scheme}://{http.Request.Host}{JwksPath}");
        });

        group.MapGet(JwksPath, (HttpContext http, SigningKey signingKey) =>
        {
            AllowCaching(http);
            return new Jwks([signingKey.PublicKey]);
        });
    }

    // Short: after a key rotation (Step 16) an old copy should not live long.
    private static void AllowCaching(HttpContext http) => http.Response.Headers.CacheControl = "public, max-age=300";
}
