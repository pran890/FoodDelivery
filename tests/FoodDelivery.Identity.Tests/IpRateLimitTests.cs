using System.Net;
using System.Net.Http.Json;
using FoodDelivery.Identity.Features.Auth;

namespace FoodDelivery.Identity.Tests;

// Limits per IP, kept in the memory of one instance.
public sealed class IpRateLimitTests(IdentityFixture fixture) : IdentityTest(fixture)
{
    private const string RequestPath = "/auth/otp/request";
    private const string VerifyPath = "/auth/otp/verify";

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string address = "192.0.2.1", string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Test-Client", address);
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        if (method == HttpMethod.Post)
        {
            // Invalid on purpose: the limiter runs before the endpoint, and nothing is written to Redis.
            request.Content = JsonContent.Create(new { email = "not-an-email", code = "000000" });
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string path, string address = "192.0.2.1", string? forwardedFor = null)
    {
        using var response = await SendAsync(client, path.StartsWith("/auth", StringComparison.Ordinal) ? HttpMethod.Post : HttpMethod.Get, path, address, forwardedFor);
        return response.StatusCode;
    }

    [Theory]
    [InlineData("IpRateLimit:OtpRequest", RequestPath)]
    [InlineData("IpRateLimit:OtpVerify", VerifyPath)]
    [InlineData("IpRateLimit:Global", "/users/00000000-0000-0000-0000-000000000000")]
    [InlineData("IpRateLimit:Global", "/no-such-page")]
    public async Task The_fourth_request_is_refused_but_another_address_is_not(string setting, string path)
    {
        await using var factory = Fixture.CreateFactory((setting, "3"));
        using var client = factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, await StatusAsync(client, path));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client, path));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await StatusAsync(client, path, "192.0.2.2"));
    }

    [Fact]
    public async Task A_refusal_is_a_problem_document()
    {
        await using var factory = Fixture.CreateFactory(("IpRateLimit:OtpVerify", "1"));
        using var client = factory.CreateClient();
        await StatusAsync(client, VerifyPath);

        using var refused = await SendAsync(client, HttpMethod.Post, VerifyPath);

        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
        Assert.InRange(refused.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
    }

    [Fact]
    public async Task The_verify_bucket_refills_one_token_at_a_time()
    {
        // 30 per minute = one token every 2 seconds.
        await using var factory = Fixture.CreateFactory(("IpRateLimit:OtpVerify", "30"));
        using var client = factory.CreateClient();
        for (var i = 0; i < 30; i++)
        {
            await StatusAsync(client, VerifyPath);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client, VerifyPath));
        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, VerifyPath));
    }

    [Fact]
    public async Task Health_checks_are_never_limited()
    {
        await using var factory = Fixture.CreateFactory(("IpRateLimit:Global", "1"));
        using var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, "/health"));
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, "/alive"));
        }
    }

    [Fact]
    public async Task A_client_cannot_choose_its_address_with_a_header()
    {
        await using var factory = Fixture.CreateFactory(("IpRateLimit:OtpRequest", "3"));
        using var client = factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            await StatusAsync(client, RequestPath, forwardedFor: $"198.51.100.{i}");
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client, RequestPath, forwardedFor: "198.51.100.99"));
    }

    [Fact]
    public async Task Addresses_in_the_same_IPv6_network_share_a_limit()
    {
        await using var factory = Fixture.CreateFactory(("IpRateLimit:OtpRequest", "1"));
        using var client = factory.CreateClient();
        await StatusAsync(client, RequestPath, "2001:db8:1:1::1");

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client, RequestPath, "2001:db8:1:1:ffff::9"));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, RequestPath, "2001:db8:1:2::1"));
    }

    [Theory]
    [InlineData("192.0.2.1", "192.0.2.1")]
    [InlineData("::ffff:192.0.2.1", "192.0.2.1")] // the same IPv4 client on an IPv6 socket
    [InlineData("2001:db8:1:1:aaaa:bbbb:cccc:dddd", "2001:db8:1:1::/64")]
    public void Client_key(string address, string expected)
    {
        Assert.Equal(expected, IpRateLimiting.ClientKey(IPAddress.Parse(address)));
    }
}
