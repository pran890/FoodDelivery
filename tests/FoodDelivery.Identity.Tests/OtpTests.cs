using System.Net;
using System.Text.RegularExpressions;
using FoodDelivery.Identity.Features.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace FoodDelivery.Identity.Tests;

public sealed class OtpTests(IdentityFixture fixture) : IdentityTest(fixture)
{
    private const string Email = "asha@example.com";
    private const string Key = "otp:" + Email;

    private static string WrongFor(string code) => code == "000000" ? "000001" : "000000";

    private async Task AssertRejectedAsync(string? email, string? code, HttpClient? client = null)
    {
        using var response = await VerifyAsync(email, code, client);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task AssertAcceptedAsync(string email, string code)
    {
        using var response = await VerifyAsync(email, code);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- what is stored ----------------------------------------------------------------------

    [Fact]
    public async Task Redis_holds_a_mac_a_counter_and_an_expiry_but_never_the_code()
    {
        var code = await GetCodeAsync(Email);

        Assert.Matches("^[0-9]{6}$", code);
        var fields = (await Fixture.Redis.HashGetAllAsync(Key)).ToDictionary(f => (string)f.Name!, f => (byte[])f.Value!);
        Assert.Equal(["attempts", "mac"], fields.Keys.Order());
        Assert.Equal(32, fields["mac"].Length);
        Assert.Equal("0"u8, fields["attempts"]);
        var ttl = await Fixture.Redis.KeyTimeToLiveAsync(Key);
        Assert.InRange(ttl!.Value.TotalSeconds, 290, 300);
    }

    [Fact]
    public async Task A_stored_mac_is_worthless_under_another_email()
    {
        var code = await GetCodeAsync(Email);
        await GetCodeAsync("victim@example.com");

        await Fixture.Redis.HashSetAsync("otp:victim@example.com", "mac", await Fixture.Redis.HashGetAsync(Key, "mac"));

        await AssertRejectedAsync("victim@example.com", code);
    }

    // ---- attempts ----------------------------------------------------------------------------

    [Fact]
    public async Task The_fifth_attempt_may_still_succeed()
    {
        var code = await GetCodeAsync(Email);
        for (var i = 0; i < 4; i++)
        {
            await AssertRejectedAsync(Email, WrongFor(code));
        }

        await AssertAcceptedAsync(Email, code);
    }

    [Fact]
    public async Task After_five_wrong_guesses_the_right_code_is_worthless()
    {
        var code = await GetCodeAsync(Email);
        for (var i = 0; i < 5; i++)
        {
            await AssertRejectedAsync(Email, WrongFor(code));
        }

        Assert.False(await Fixture.Redis.KeyExistsAsync(Key));
        await AssertRejectedAsync(Email, code);
    }

    [Fact]
    public async Task Of_100_simultaneous_guesses_only_five_are_compared()
    {
        await GetCodeAsync(Email);
        await using var scope = Fixture.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<OtpStore>();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => store.TakeAttemptAsync(Email)));

        Assert.Equal([1, 2, 3, 4, 5], attempts.Where(a => a is not null).Select(a => a!.Value.Number).Order());
    }

    [Fact]
    public async Task A_guess_does_not_extend_the_expiry()
    {
        var code = await GetCodeAsync(Email);
        await Fixture.Redis.KeyExpireAsync(Key, TimeSpan.FromSeconds(60));

        await AssertRejectedAsync(Email, WrongFor(code));

        var ttl = await Fixture.Redis.KeyTimeToLiveAsync(Key);
        Assert.InRange(ttl!.Value.TotalSeconds, 50, 60);
    }

    // ---- single use and expiry ---------------------------------------------------------------

    [Fact]
    public async Task A_code_works_once()
    {
        var code = await GetCodeAsync(Email);

        await AssertAcceptedAsync(Email, code);
        await AssertRejectedAsync(Email, code);
    }

    // Forced, like the race in Step 08: Redis holds back all writes until every request is waiting,
    // so all of them compare the right code before any of them can delete it.
    [Fact]
    public async Task Of_simultaneous_correct_requests_exactly_one_succeeds()
    {
        var code = await GetCodeAsync(Email);
        await Fixture.Redis.ExecuteAsync("CLIENT", "PAUSE", "1500", "WRITE");

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => VerifyAsync(Email, code)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(4, responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task An_expired_code_is_rejected_and_leaves_nothing_behind()
    {
        await using var shortLived = Fixture.CreateFactory(otpLifetime: "00:00:01");
        using var client = shortLived.CreateClient();
        var code = await GetCodeAsync(Email, client);
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);

        await AssertRejectedAsync(Email, code, client);

        Assert.False(await Fixture.Redis.KeyExistsAsync(Key));
    }

    [Fact]
    public async Task A_new_request_replaces_the_old_code_and_its_attempts()
    {
        var first = await GetCodeAsync(Email);
        await AssertRejectedAsync(Email, WrongFor(first));
        var second = await GetCodeAsync(Email);

        Assert.Equal(0, await Fixture.Redis.HashGetAsync(Key, "attempts"));
        if (first != second) // one in a million
        {
            await AssertRejectedAsync(Email, first);
        }

        await AssertAcceptedAsync(Email, second);
    }

    // ---- no hints ----------------------------------------------------------------------------

    [Fact]
    public async Task Known_and_unknown_emails_get_the_same_answer()
    {
        await SignInAsync(Email);

        using var known = await RequestCodeAsync(Email);
        using var unknown = await RequestCodeAsync("nobody@example.com");

        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(await DescribeAsync(unknown), await DescribeAsync(known));
    }

    [Fact]
    public async Task Every_failure_gets_the_same_answer()
    {
        var code = await GetCodeAsync(Email);

        using var wrong = await VerifyAsync(Email, WrongFor(code));
        using var neverRequested = await VerifyAsync("nobody@example.com", code);
        using var noCode = await VerifyAsync(Email, null);
        using var badEmail = await VerifyAsync("not-an-email", code);

        var expected = await DescribeAsync(wrong);
        Assert.Contains("Invalid or expired code.", expected, StringComparison.Ordinal);
        Assert.Equal(expected, await DescribeAsync(neverRequested));
        Assert.Equal(expected, await DescribeAsync(noCode));
        Assert.Equal(expected, await DescribeAsync(badEmail));
    }

    [Fact]
    public async Task Malformed_email_is_a_bad_request()
    {
        using var response = await RequestCodeAsync("not-an-email");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- things that break -------------------------------------------------------------------

    [Fact]
    public async Task Mail_server_down_is_still_202()
    {
        Fixture.MakeSendingFail();

        using var response = await RequestCodeAsync(Email);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Redis_down_is_503_for_sign_in_but_only_degraded_for_the_service()
    {
        await using var factory = Fixture.CreateFactory(redis: "127.0.0.1:1,connectTimeout=500,asyncTimeout=1000");
        using var client = factory.CreateClient();

        using var request = await RequestCodeAsync(Email, client);
        using var health = await client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, request.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Contains("\"status\":\"Degraded\"", await health.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    /// <summary>Everything a client can see, except the trace id, which always differs.</summary>
    private static async Task<string> DescribeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        return $"{(int)response.StatusCode}\n{response.Headers}{response.Content.Headers.ContentType}\n{Regex.Replace(body, "\"traceId\":\"[^\"]*\"", "")}";
    }
}
