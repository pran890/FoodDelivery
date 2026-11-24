using System.Net;

namespace FoodDelivery.Sandbox.Kitchen.Tests;

public sealed class HealthTests(KitchenFixture fixture) : KitchenTest(fixture)
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task Probes_are_healthy_when_everything_is_up(string path)
    {
        using var response = await Client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Step 05's most important rule, now enforced: a dead database fails readiness, never liveness.
    [Fact]
    public async Task Dead_postgres_fails_readiness_but_not_liveness()
    {
        // Its own Kitchen with a wrong port, so the shared one keeps working for other tests.
        await using var factory = KitchenFixture.CreateFactory(
            postgres: "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x",
            redis: Fixture.RedisConnectionString);
        using var client = factory.CreateClient();

        using var health = await client.GetAsync("/health", Ct);
        using var alive = await client.GetAsync("/alive", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
    }
}
