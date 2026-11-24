using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FoodDelivery.Catalog.Tests;

/// <summary>
/// Stands in for Identity on the network: it serves the two public addresses and signs tokens. Everything
/// else is real: service discovery, retries, key caching and the JWT checks. Catalog never learns the difference.
/// </summary>
public sealed class FakeIdentity : HttpMessageHandler
{
    private static readonly JsonWebTokenHandler _handler = new();

    private RsaSecurityKey _key = NewKey();

    public bool IsDown { get; set; }

    public int KeyDownloads { get; private set; }

    public WebApplicationFactory<Program> CreateCatalog(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            foreach (var (key, value) in settings)
            {
                host.UseSetting(key, value);
            }

            host.ConfigureTestServices(services => services
                .AddHttpClient(JwtExtensions.KeysHttpClient)
                .ConfigurePrimaryHttpMessageHandler(() => new Forwarder(this)));
        });

    /// <summary>From now on tokens are signed with another key, and only that one is published.</summary>
    public void RotateKey() => _key = NewKey();

    /// <summary>A token like Identity's, with anything changed that a test wants changed.</summary>
    public string Token(
        string issuer = "https://identity.fooddelivery.local",
        string audience = "fooddelivery-api",
        string type = "at+jwt",
        int notBeforeSeconds = 0,
        int expiresSeconds = 900,
        string[]? roles = null,
        RsaSecurityKey? key = null) => _handler.CreateToken(new SecurityTokenDescriptor
        {
            TokenType = type,
            Issuer = issuer,
            Audience = audience,
            IssuedAt = DateTime.UtcNow.AddMinutes(-30),
            NotBefore = notBeforeSeconds > 0 ? DateTime.UtcNow.AddSeconds(notBeforeSeconds) : DateTime.UtcNow.AddMinutes(-30),
            Expires = DateTime.UtcNow.AddSeconds(expiresSeconds),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "01a20cea-b012-74ed-b014-b977996e0202",
                ["email"] = "asha@example.com",
                ["role"] = roles ?? ["Customer"],
            },
            SigningCredentials = new SigningCredentials(key ?? _key, SecurityAlgorithms.RsaSha256),
        });

    /// <summary>An attacker's key that claims to be the real one.</summary>
    public RsaSecurityKey ForgedKey()
    {
        var key = NewKey();
        key.KeyId = _key.KeyId;
        return key;
    }

    public string PublicKeyPem()
    {
        using var rsa = RSA.Create(_key.Rsa.ExportParameters(includePrivateParameters: false));
        return rsa.ExportSubjectPublicKeyInfoPem();
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (IsDown)
        {
            throw new HttpRequestException("Identity is down.");
        }

        if (request.RequestUri!.AbsolutePath == "/.well-known/openid-configuration")
        {
            return Json(new { issuer = "https://identity.fooddelivery.local", jwks_uri = "https://identity/.well-known/jwks.json" });
        }

        KeyDownloads++;
        var parameters = _key.Rsa.ExportParameters(includePrivateParameters: false);
        return Json(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = _key.KeyId,
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent),
                },
            },
        });
    }

    private static Task<HttpResponseMessage> Json(object body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });

    private static RsaSecurityKey NewKey()
    {
        // Its own signing cache: see the bug CI found in Step 11.
        var key = new RsaSecurityKey(RSA.Create(2048)) { CryptoProviderFactory = new CryptoProviderFactory() };
        key.KeyId = Base64UrlEncoder.Encode(key.ComputeJwkThumbprint());
        return key;
    }

    /// <summary>The factory disposes the handlers it creates; the fake must outlive them.</summary>
    private sealed class Forwarder(FakeIdentity identity) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            identity.SendAsync(request, cancellationToken);
    }
}
