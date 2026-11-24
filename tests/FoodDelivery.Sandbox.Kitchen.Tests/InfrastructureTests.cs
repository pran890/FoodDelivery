using System.Net.Http.Json;

namespace FoodDelivery.Sandbox.Kitchen.Tests;

public sealed class InfrastructureTests(KitchenFixture fixture) : KitchenTest(fixture)
{
    // A fake or an in-memory database could never tell us this.
    [Fact]
    public async Task Postgres_has_postgis_available()
    {
        var info = await Client.GetFromJsonAsync<PostgresInfo>("/infra/postgres", Ct);

        Assert.NotNull(info?.PostgisAvailable);
    }

    // These two tests expect a fresh counter. They only both pass because state is reset before
    // each test. Remove the reset and the second one to run fails (Step 06, experiment 1).
    [Fact]
    public async Task First_call_counts_one_order()
    {
        var info = await Client.GetFromJsonAsync<RedisInfo>("/infra/redis", Ct);

        Assert.Equal(1, info?.OrdersServed);
    }

    [Fact]
    public async Task Three_calls_count_three_orders()
    {
        RedisInfo? info = null;
        for (var i = 0; i < 3; i++)
        {
            info = await Client.GetFromJsonAsync<RedisInfo>("/infra/redis", Ct);
        }

        Assert.Equal(3, info?.OrdersServed);
    }
}
