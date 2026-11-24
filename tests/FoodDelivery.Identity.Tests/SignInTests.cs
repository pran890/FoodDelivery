using System.Net;
using System.Net.Http.Json;
using FoodDelivery.Identity.Features.Auth;
using FoodDelivery.Identity.Features.Users;
using Npgsql;

namespace FoodDelivery.Identity.Tests;

public sealed class SignInTests(IdentityFixture fixture) : IdentityTest(fixture)
{
    [Fact]
    public async Task First_sign_in_creates_a_customer_that_can_be_read_back()
    {
        var user = await SignInAsync("asha@example.com");
        var read = await Client.GetFromJsonAsync<GetUser.Response>($"/users/{user.Id}", Ct);

        Assert.Equal("asha@example.com", user.Email);
        Assert.Equal(["Customer"], user.Roles);
        Assert.Equal(user.Id, read?.Id);
        Assert.Equal(["Customer"], read?.Roles);
    }

    [Fact]
    public async Task Signing_in_again_is_the_same_user_whatever_the_case()
    {
        var first = await SignInAsync("asha@example.com");

        await GetCodeAsync("asha@example.com");
        using var response = await VerifyAsync(" ASHA@Example.com ", $" {Fixture.CodeFor("asha@example.com")} ");
        var second = await response.Content.ReadFromJsonAsync<VerifyOtp.Response>(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(first.Id, second?.Id);
    }

    // Step 08's race, forced the same way: a lock lets the "does this user exist?" read through but holds
    // back the INSERT. Meanwhile somebody else creates the user.
    [Fact]
    public async Task A_user_created_at_the_same_moment_is_used_and_not_an_error()
    {
        var code = await GetCodeAsync("race@example.com");
        var existing = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(Fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await ScalarAsync("LOCK TABLE users IN EXCLUSIVE MODE");

        var verify = VerifyAsync("race@example.com", code);
        while (!verify.IsCompleted && (long)(await ScalarAsync(
            "SELECT count(*) FROM pg_locks WHERE relation = 'users'::regclass AND NOT granted"))! == 0)
        {
            await Task.Delay(20, Ct);
        }

        Assert.False(verify.IsCompleted, "The request never reached its INSERT.");
        await ScalarAsync($"INSERT INTO users (id, email, created_at) VALUES ('{existing}', 'race@example.com', now())");
        await transaction.CommitAsync(Ct);

        using var response = await verify;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(existing, (await response.Content.ReadFromJsonAsync<VerifyOtp.Response>(Ct))?.Id);

        async Task<object?> ScalarAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            return await command.ExecuteScalarAsync(Ct);
        }
    }

    [Fact]
    public async Task Unknown_user_is_not_found()
    {
        using var response = await Client.GetAsync($"/users/{Guid.CreateVersion7()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // GET /users/{id} has no login yet, so it must not exist outside Development. Signing in must.
    [Fact]
    public async Task In_production_users_cannot_be_read_but_sign_in_works()
    {
        var user = await SignInAsync("asha@example.com");
        await using var production = Fixture.CreateFactory(("environment", "Production"));
        using var client = production.CreateClient();

        using var read = await client.GetAsync($"/users/{user.Id}", Ct);
        using var request = await RequestCodeAsync("asha@example.com", client);

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
    }
}
