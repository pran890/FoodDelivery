using System.Net;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FoodDelivery.Identity.Tests;

public sealed class WellKnownTests(IdentityFixture fixture) : IdentityTest(fixture)
{
    private async Task<JsonObject> GetJsonAsync(string path, HttpClient? client = null) =>
        JsonNode.Parse(await (client ?? Client).GetStringAsync(path, Ct))!.AsObject();

    [Fact]
    public async Task Discovery_names_the_issuer_and_the_keys_address()
    {
        var discovery = await GetJsonAsync("/.well-known/openid-configuration");

        Assert.Equal(["issuer", "jwks_uri"], discovery.Select(p => p.Key).Order());
        Assert.Equal("https://identity.fooddelivery.local", (string?)discovery["issuer"]);
        Assert.Equal("http://localhost/.well-known/jwks.json", (string?)discovery["jwks_uri"]);
    }

    // The private key has the parts d, p, q, dp, dq and qi. None may ever be here.
    [Fact]
    public async Task Jwks_contains_only_the_public_half()
    {
        var keys = (await GetJsonAsync("/.well-known/jwks.json"))["keys"]!.AsArray();

        var key = Assert.Single(keys)!.AsObject();
        Assert.Equal(["alg", "e", "kid", "kty", "n", "use"], key.Select(p => p.Key).Order());
        Assert.Equal("RS256", (string?)key["alg"]);
        Assert.Equal("sig", (string?)key["use"]);
    }

    [Fact]
    public async Task A_token_can_be_checked_with_nothing_but_the_published_key()
    {
        var token = (await SignInAsync("asha@example.com")).AccessToken;
        var jwks = new JsonWebKeySet(await Client.GetStringAsync("/.well-known/jwks.json", Ct));

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "https://identity.fooddelivery.local",
            ValidAudience = "fooddelivery-api",
            IssuerSigningKeys = jwks.GetSigningKeys(),
        });

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal(jwks.Keys[0].Kid, new JsonWebToken(token).Kid);
    }

    [Theory]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/.well-known/jwks.json")]
    public async Task Public_cacheable_never_limited_and_there_in_production(string path)
    {
        await using var factory = Fixture.CreateFactory(("environment", "Production"), ("IpRateLimit:Global", "1"));
        using var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            using var response = await client.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(TimeSpan.FromMinutes(5), response.Headers.CacheControl?.MaxAge);
            Assert.True(response.Headers.CacheControl?.Public);
        }
    }
}
