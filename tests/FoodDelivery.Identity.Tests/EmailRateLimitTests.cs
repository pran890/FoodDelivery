using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using StackExchange.Redis;

namespace FoodDelivery.Identity.Tests;

// Limits per EMAIL, kept in Redis.
public sealed class EmailRateLimitTests(IdentityFixture fixture) : IdentityTest(fixture)
{
    private const string Email = "asha@example.com";
    private const string Key = "rl:otp:" + Email;

    private WebApplicationFactory<Program> WithLimits(string gap = "00:00:00", string perHour = "1000", string perDay = "1000") =>
        Fixture.CreateFactory(("OtpRateLimit:Gap", gap), ("OtpRateLimit:PerHour", perHour), ("OtpRateLimit:PerDay", perDay));

    private async Task<HttpStatusCode> StatusAsync(HttpClient client, string email = Email)
    {
        using var response = await RequestCodeAsync(email, client);
        return response.StatusCode;
    }

    /// <summary>Makes all stored requests look older, instead of waiting.</summary>
    private async Task AgeAsync(TimeSpan by)
    {
        foreach (var entry in await Fixture.Redis.SortedSetRangeByRankWithScoresAsync(Key))
        {
            await Fixture.Redis.SortedSetAddAsync(Key, entry.Element, entry.Score - by.TotalMilliseconds);
        }
    }

    [Fact]
    public async Task Second_request_within_the_gap_is_refused_and_the_first_code_still_works()
    {
        await using var factory = WithLimits(gap: "00:01:00");
        using var client = factory.CreateClient();
        var code = await GetCodeAsync(Email, client);

        using var refused = await RequestCodeAsync(Email, client);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.InRange(refused.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 50, 60);
        Assert.Equal(code, Fixture.CodeFor(Email)); // no second email
        using var verify = await VerifyAsync(Email, code, client);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
    }

    [Fact]
    public async Task After_the_gap_the_next_request_is_allowed()
    {
        await using var factory = WithLimits(gap: "00:01:00");
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted, await StatusAsync(client));

        await AgeAsync(TimeSpan.FromSeconds(59));
        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client));
        await AgeAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Accepted, await StatusAsync(client));
    }

    [Theory]
    [InlineData("3", "1000", 59, 2)]           // per hour: still refused after 59 min, allowed after 61
    [InlineData("1000", "3", 23 * 60 + 59, 2)] // per day: still refused after 23 h 59, allowed after 24 h 01
    public async Task Window_limits_hold_exactly_as_long_as_the_window(string perHour, string perDay, int refusedAfterMinutes, int thenMinutes)
    {
        await using var factory = WithLimits(perHour: perHour, perDay: perDay);
        using var client = factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, await StatusAsync(client));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client));
        await AgeAsync(TimeSpan.FromMinutes(refusedAfterMinutes));
        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client));
        await AgeAsync(TimeSpan.FromMinutes(thenMinutes));
        Assert.Equal(HttpStatusCode.Accepted, await StatusAsync(client));
    }

    [Fact]
    public async Task Refused_requests_are_not_stored_and_do_not_extend_anything()
    {
        await using var factory = WithLimits(perHour: "2");
        using var client = factory.CreateClient();
        await StatusAsync(client);
        await StatusAsync(client);
        await Fixture.Redis.KeyExpireAsync(Key, TimeSpan.FromMinutes(10));

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client));
        }

        Assert.Equal(2, await Fixture.Redis.SortedSetLengthAsync(Key));
        Assert.InRange((await Fixture.Redis.KeyTimeToLiveAsync(Key))!.Value.TotalMinutes, 9, 10);
    }

    [Fact]
    public async Task An_accepted_request_is_kept_for_24_hours()
    {
        Assert.Equal(HttpStatusCode.Accepted, await StatusAsync(Client));

        Assert.Equal(RedisType.SortedSet, await Fixture.Redis.KeyTypeAsync(Key));
        Assert.InRange((await Fixture.Redis.KeyTimeToLiveAsync(Key))!.Value.TotalHours, 23.9, 24);
    }

    [Fact]
    public async Task Of_50_simultaneous_requests_exactly_the_limit_gets_through()
    {
        await using var factory = WithLimits(perHour: "3");
        using var client = factory.CreateClient();

        var statuses = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => StatusAsync(client)));

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Accepted));
        Assert.Equal(47, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task The_limit_is_shared_by_all_instances_and_by_all_spellings()
    {
        await using var first = WithLimits(perHour: "1");
        await using var second = WithLimits(perHour: "1");
        using var firstClient = first.CreateClient();
        using var secondClient = second.CreateClient();

        Assert.Equal(HttpStatusCode.Accepted, await StatusAsync(firstClient));
        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(secondClient, " ASHA@example.com "));
    }

    [Fact]
    public async Task Another_email_is_not_affected()
    {
        await using var factory = WithLimits(perHour: "1");
        using var client = factory.CreateClient();
        await StatusAsync(client);

        Assert.Equal(HttpStatusCode.Accepted, await StatusAsync(client, "ravi@example.com"));
    }

    [Fact]
    public async Task Known_and_unknown_emails_are_limited_alike()
    {
        await SignInAsync(Email); // also counts one request
        await StatusAsync(Client, "nobody@example.com");
        await using var factory = WithLimits(perHour: "1");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client));
        Assert.Equal(HttpStatusCode.TooManyRequests, await StatusAsync(client, "nobody@example.com"));
    }
}
