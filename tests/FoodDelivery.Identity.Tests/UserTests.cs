using System.Net;
using System.Net.Http.Json;
using FoodDelivery.Identity.Features.Users;
using Npgsql;

namespace FoodDelivery.Identity.Tests;

public sealed class UserTests(IdentityFixture fixture) : IdentityTest(fixture)
{
    private Task<HttpResponseMessage> PostAsync(string? email) =>
        Client.PostAsJsonAsync("/users", new CreateUser.Request(email), Ct);

    [Fact]
    public async Task Created_user_can_be_read_back_unchanged()
    {
        using var response = await PostAsync(" Asha@Example.com ");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreateUser.Response>(Ct);
        var read = await Client.GetFromJsonAsync<GetUser.Response>(response.Headers.Location, Ct);

        Assert.NotNull(created);
        Assert.NotNull(read);
        Assert.Equal("asha@example.com", created.Email);
        Assert.Equal(["Customer"], read.Roles);
        Assert.Equal(created.Id, read.Id);
        // Exactly equal: the time was rounded to what Postgres can store.
        Assert.Equal(created.CreatedAt, read.CreatedAt);
    }

    [Fact]
    public async Task Same_email_in_other_case_is_a_conflict()
    {
        using var first = await PostAsync("asha@example.com");
        using var second = await PostAsync("ASHA@example.com");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    // Just sending requests in parallel would catch a race only sometimes. Here the race is forced:
    // a lock lets reads through but holds back every INSERT until all requests have reached theirs.
    [Fact]
    public async Task Simultaneous_sign_ups_create_exactly_one_user()
    {
        const int requests = 5;
        await using var connection = new NpgsqlConnection(Fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await ExecuteAsync("LOCK TABLE users IN EXCLUSIVE MODE");

        var responses = Enumerable.Range(0, requests).Select(_ => PostAsync("race@example.com")).ToArray();
        // A request that finishes while the lock is held never reached its INSERT: stop waiting, fail below.
        while (await CountWaitingInsertsAsync() < requests && !responses.Any(r => r.IsCompleted))
        {
            await Task.Delay(20, Ct);
        }

        await transaction.CommitAsync(Ct); // releases the lock: all INSERTs go at once
        var statuses = (await Task.WhenAll(responses)).Select(r => r.StatusCode).Order();

        Assert.Equal([HttpStatusCode.Created, .. Enumerable.Repeat(HttpStatusCode.Conflict, requests - 1)], statuses);

        async Task ExecuteAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        async Task<long> CountWaitingInsertsAsync()
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks WHERE relation = 'users'::regclass AND NOT granted", connection);
            return (long)(await command.ExecuteScalarAsync(Ct))!;
        }
    }

    [Fact]
    public async Task Invalid_email_is_a_bad_request()
    {
        using var response = await PostAsync("not-an-email");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_user_is_not_found()
    {
        using var response = await Client.GetAsync($"/users/{Guid.CreateVersion7()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // These endpoints have no login yet, so they must not exist outside Development.
    [Fact]
    public async Task User_endpoints_do_not_exist_in_production()
    {
        await using var production = Fixture.CreateFactory("Production");
        using var client = production.CreateClient();

        using var post = await client.PostAsJsonAsync("/users", new CreateUser.Request("asha@example.com"), Ct);
        using var get = await client.GetAsync($"/users/{Guid.CreateVersion7()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }
}
