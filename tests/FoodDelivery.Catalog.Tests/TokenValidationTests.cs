using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FoodDelivery.Catalog.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;

namespace FoodDelivery.Catalog.Tests;

// xUnit creates this class anew for every test, so each test has its own Catalog and its own fake Identity.
public sealed class TokenValidationTests : IAsyncDisposable
{
    private readonly FakeIdentity _identity = new();
    private readonly WebApplicationFactory<Program> _catalog;
    private readonly HttpClient _client;

    public TokenValidationTests()
    {
        _catalog = _identity.CreateCatalog();
        _client = _catalog.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpResponseMessage> GetAsync(string? token, string path = "/whoami")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _client.SendAsync(request, Ct);
    }

    private async Task<HttpStatusCode> StatusAsync(string? token, string path = "/whoami")
    {
        using var response = await GetAsync(token, path);
        return response.StatusCode;
    }

    // ---- accepted ----------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_token_tells_who_the_caller_is()
    {
        using var response = await GetAsync(_identity.Token(roles: ["Customer", "Admin"]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<WhoAmI.Response>(Ct);
        Assert.Equal("01a20cea-b012-74ed-b014-b977996e0202", me?.Id);
        Assert.Equal("asha@example.com", me?.Email);
        Assert.Equal(["Customer", "Admin"], me?.Roles);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task Probes_need_no_token(string path)
    {
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(null, path));
    }

    // ---- refused -----------------------------------------------------------------------------

    [Fact]
    public async Task No_token_is_401_and_says_which_kind_is_expected()
    {
        using var response = await GetAsync(null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Everything_not_marked_public_is_protected()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(null, "/no-such-page"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(_identity.Token(), "/no-such-page"));
    }

    [Theory]
    [InlineData("not a token")]
    [InlineData("a.b.c")]
    [InlineData("")]
    public async Task Nonsense_is_401(string token)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(token));
    }

    [Fact]
    public async Task Wrong_audience_issuer_or_type_is_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(_identity.Token(audience: "another-api")));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(_identity.Token(issuer: "https://evil.example")));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(_identity.Token(type: "JWT")));
    }

    // 30 seconds of clock difference are allowed, in both directions.
    [Theory]
    [InlineData(-20, HttpStatusCode.OK)]
    [InlineData(-40, HttpStatusCode.Unauthorized)]
    public async Task Expired_tokens(int expiresSeconds, HttpStatusCode expected)
    {
        Assert.Equal(expected, await StatusAsync(_identity.Token(expiresSeconds: expiresSeconds)));
    }

    [Theory]
    [InlineData(20, HttpStatusCode.OK)]
    [InlineData(40, HttpStatusCode.Unauthorized)]
    public async Task Tokens_from_the_future(int notBeforeSeconds, HttpStatusCode expected)
    {
        Assert.Equal(expected, await StatusAsync(_identity.Token(notBeforeSeconds: notBeforeSeconds)));
    }

    [Fact]
    public async Task Forged_tokens_are_401()
    {
        var parts = _identity.Token().Split('.');
        var claims = JsonNode.Parse(Base64UrlEncoder.DecodeBytes(parts[1]))!.AsObject();
        claims["role"] = new JsonArray("Admin");
        var changedClaims = Encode(claims.ToJsonString());
        var none = Encode("""{"alg":"none","typ":"at+jwt"}""");
        var hs256 = $"{Encode("""{"alg":"HS256","typ":"at+jwt"}""")}.{changedClaims}";
        var hmac = HMACSHA256.HashData(Encoding.ASCII.GetBytes(_identity.PublicKeyPem()), Encoding.ASCII.GetBytes(hs256));

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(string.Join('.', parts))); // the original is fine
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync($"{parts[0]}.{changedClaims}.{parts[2]}"));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync($"{none}.{changedClaims}."));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync($"{hs256}.{Base64UrlEncoder.Encode(hmac)}"));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(_identity.Token(key: _identity.ForgedKey())));
    }

    // ---- keys --------------------------------------------------------------------------------

    [Fact]
    public async Task Keys_are_downloaded_once_and_then_kept()
    {
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(_identity.Token()));
        }

        Assert.Equal(1, _identity.KeyDownloads);
    }

    [Fact]
    public async Task Identity_may_be_down_once_the_keys_are_known()
    {
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(_identity.Token()));
        _identity.IsDown = true;

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(_identity.Token()));
    }

    // Without keys we can't say that a token is bad, so it must not be 401.
    [Fact]
    public async Task Identity_down_before_the_keys_are_known_is_503_and_recovers()
    {
        _identity.IsDown = true;
        using var unavailable = await GetAsync(_identity.Token());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.NotNull(unavailable.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(null)); // no token is still 401

        _identity.IsDown = false;
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(_identity.Token()));
    }

    [Fact]
    public async Task A_new_key_is_picked_up()
    {
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(_identity.Token()));
        _identity.RotateKey();

        // The unknown key makes Catalog download the keys again in the background.
        var status = await StatusAsync(_identity.Token());
        for (var i = 0; i < 50 && status != HttpStatusCode.OK; i++)
        {
            await Task.Delay(100, Ct);
            status = await StatusAsync(_identity.Token());
        }

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, _identity.KeyDownloads);
    }

    [Fact]
    public async Task Unknown_keys_cannot_make_Catalog_hammer_Identity()
    {
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(_identity.Token()));
        using var attacker = new FakeIdentity();
        for (var i = 0; i < 20; i++)
        {
            attacker.RotateKey();
            Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(attacker.Token()));
        }

        Assert.InRange(_identity.KeyDownloads, 1, 2);
    }

    // Keys fetched over plain http could be replaced on the way, and with them everyone's identity.
    [Fact]
    public async Task Production_starts_only_with_an_https_authority()
    {
        await using var unset = _identity.CreateCatalog(("environment", "Production"));
        await using var plain = _identity.CreateCatalog(("environment", "Production"), ("Jwt:Authority", "http://identity"));
        await using var secure = _identity.CreateCatalog(("environment", "Production"), ("Jwt:Authority", "https://identity"));

        Assert.Throws<InvalidOperationException>(() => unset.CreateClient());
        Assert.Throws<InvalidOperationException>(() => plain.CreateClient());
        using var client = secure.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/whoami", Ct)).StatusCode);
    }

    private static string Encode(string text) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(text));

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _catalog.DisposeAsync();
        _identity.Dispose();
    }
}
