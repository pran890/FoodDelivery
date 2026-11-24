using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FoodDelivery.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FoodDelivery.Identity.Tests;

public sealed class TokenTests(IdentityFixture fixture) : IdentityTest(fixture)
{
    private const string Email = "asha@example.com";

    private static readonly JsonWebTokenHandler _handler = new();
    private static readonly string[] _adminRole = ["Admin"];

    private async Task<string> GetTokenAsync(HttpClient? client = null) => (await SignInAsync(Email, client)).AccessToken;

    /// <summary>Only the PUBLIC half, which is all that other services will ever have.</summary>
    private RsaSecurityKey PublicKey()
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(Fixture.SigningKeyPath));
        var key = new RsaSecurityKey(rsa.ExportParameters(includePrivateParameters: false));
        key.KeyId = Base64UrlEncoder.Encode(key.ComputeJwkThumbprint());
        return key;
    }

    /// <summary>The checks a service should make. Step 12 sets the same ones for real.</summary>
    private Task<TokenValidationResult> ValidateAsync(string token) =>
        _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "https://identity.fooddelivery.local",
            ValidAudience = "fooddelivery-api",
            ValidTypes = ["at+jwt"],
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKey = PublicKey(),
            ClockSkew = TimeSpan.Zero,
        });

    private async Task AssertRejectedAsync<TException>(string token) where TException : Exception
    {
        var result = await ValidateAsync(token);
        Assert.False(result.IsValid);
        Assert.IsType<TException>(result.Exception);
    }

    private static JsonObject Decode(string part) => JsonNode.Parse(Base64UrlEncoder.DecodeBytes(part))!.AsObject();

    private static string Encode(JsonObject json) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(json.ToJsonString()));

    // ---- the contract ------------------------------------------------------------------------

    [Fact]
    public async Task Header_and_claims_are_exactly_the_agreed_ones()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signedIn = await SignInAsync(Email);
        var parts = signedIn.AccessToken.Split('.');
        Assert.Equal(3, parts.Length);
        var header = Decode(parts[0]);
        var claims = Decode(parts[1]);

        Assert.Equal(["alg", "kid", "typ"], header.Select(p => p.Key).Order());
        Assert.Equal("RS256", (string?)header["alg"]);
        Assert.Equal("at+jwt", (string?)header["typ"]);
        Assert.Equal(PublicKey().KeyId, (string?)header["kid"]);

        Assert.Equal(["aud", "email", "exp", "iat", "iss", "jti", "nbf", "role", "sub"], claims.Select(p => p.Key).Order());
        Assert.Equal("https://identity.fooddelivery.local", (string?)claims["iss"]);
        Assert.Equal("fooddelivery-api", (string?)claims["aud"]);
        Assert.Equal(signedIn.User.Id.ToString(), (string?)claims["sub"]);
        Assert.Equal(Email, (string?)claims["email"]);
        Assert.Equal("""["Customer"]""", claims["role"]!.ToJsonString()); // a list, even with one role
        Assert.InRange((long)claims["iat"]!, before, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Equal((long)claims["iat"]!, (long)claims["nbf"]!);
        Assert.Equal((long)claims["iat"]! + 900, (long)claims["exp"]!);
    }

    [Fact]
    public async Task Response_is_a_bearer_token_that_must_not_be_cached()
    {
        using var response = await VerifyAsync(Email, await GetCodeAsync(Email));

        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
        Assert.Equal(["accessToken", "expiresIn", "tokenType", "user"], json.Select(p => p.Key).Order());
        Assert.Equal("Bearer", (string?)json["tokenType"]);
        Assert.Equal(900, (int?)json["expiresIn"]);
    }

    [Fact]
    public async Task Every_token_has_its_own_id()
    {
        var first = Decode((await GetTokenAsync()).Split('.')[1]);
        var second = Decode((await GetTokenAsync()).Split('.')[1]);

        Assert.NotEqual((string?)first["jti"], (string?)second["jti"]);
        Assert.Equal((string?)first["sub"], (string?)second["sub"]);
    }

    [Fact]
    public async Task All_roles_are_in_the_token()
    {
        await using (var scope = Fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            await SignInAsync(Email);
            var user = await db.Users.SingleAsync(Ct);
            db.Add(new UserRole { UserId = user.Id, RoleId = RoleId.Admin, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        var claims = Decode((await GetTokenAsync()).Split('.')[1]);

        Assert.Equal("""["Customer","Admin"]""", claims["role"]!.ToJsonString());
    }

    // ---- attacks -----------------------------------------------------------------------------

    [Fact]
    public async Task A_genuine_token_is_accepted()
    {
        var result = await ValidateAsync(await GetTokenAsync());

        Assert.True(result.IsValid, result.Exception?.Message);
    }

    [Fact]
    public async Task Changing_a_role_breaks_the_signature()
    {
        var parts = (await GetTokenAsync()).Split('.');
        var claims = Decode(parts[1]);
        claims["role"] = new JsonArray("Admin");

        await AssertRejectedAsync<SecurityTokenInvalidSignatureException>($"{parts[0]}.{Encode(claims)}.{parts[2]}");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("None")]
    [InlineData("nOnE")]
    public async Task A_token_without_signature_is_rejected(string algorithm)
    {
        var parts = (await GetTokenAsync()).Split('.');
        var header = Decode(parts[0]);
        header["alg"] = algorithm;
        var claims = Decode(parts[1]);
        claims["role"] = new JsonArray("Admin");

        await AssertRejectedAsync<SecurityTokenInvalidSignatureException>($"{Encode(header)}.{Encode(claims)}.");
    }

    // The classic attack: the public key is public, so use it as the shared secret of HS256.
    [Fact]
    public async Task A_token_signed_with_the_public_key_as_a_secret_is_rejected()
    {
        var parts = (await GetTokenAsync()).Split('.');
        var header = Decode(parts[0]);
        header["alg"] = "HS256";
        var unsigned = $"{Encode(header)}.{parts[1]}";
        using var rsa = RSA.Create(PublicKey().Parameters);
        var signature = HMACSHA256.HashData(
            Encoding.ASCII.GetBytes(rsa.ExportSubjectPublicKeyInfoPem()), Encoding.ASCII.GetBytes(unsigned));

        var result = await ValidateAsync($"{unsigned}.{Base64UrlEncoder.Encode(signature)}");

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task A_token_from_another_key_with_the_same_key_id_is_rejected()
    {
        using var rsa = RSA.Create(2048);
        var attacker = new RsaSecurityKey(rsa) { KeyId = PublicKey().KeyId };

        await AssertRejectedAsync<SecurityTokenInvalidSignatureException>(Forge(attacker, "at+jwt"));
    }

    // Signed with the REAL key, but made for another purpose.
    [Fact]
    public async Task Another_type_of_token_is_rejected()
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(await File.ReadAllTextAsync(EnsureKeyFile(), Ct));
        var realKey = new RsaSecurityKey(rsa) { KeyId = PublicKey().KeyId };

        Assert.True((await ValidateAsync(Forge(realKey, "at+jwt"))).IsValid); // the forgery itself is fine...
        await AssertRejectedAsync<SecurityTokenInvalidTypeException>(Forge(realKey, "JWT")); // ...except for its type
    }

    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        await using var factory = Fixture.CreateFactory(("Jwt:Lifetime", "00:00:01"));
        using var client = factory.CreateClient();
        var token = await GetTokenAsync(client);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        await AssertRejectedAsync<SecurityTokenExpiredException>(token);
    }

    private string EnsureKeyFile()
    {
        Assert.True(File.Exists(Fixture.SigningKeyPath));
        return Fixture.SigningKeyPath;
    }

    private static string Forge(RsaSecurityKey key, string type) => _handler.CreateToken(new SecurityTokenDescriptor
    {
        TokenType = type,
        Issuer = "https://identity.fooddelivery.local",
        Audience = "fooddelivery-api",
        Expires = DateTime.UtcNow.AddMinutes(5),
        Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString(), ["role"] = _adminRole },
        SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
    });
}
