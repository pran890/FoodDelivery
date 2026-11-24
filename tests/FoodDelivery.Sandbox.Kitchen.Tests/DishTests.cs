using System.Net;
using System.Net.Http.Json;

namespace FoodDelivery.Sandbox.Kitchen.Tests;

public sealed class DishTests(KitchenFixture fixture) : KitchenTest(fixture)
{
    [Fact]
    public async Task Dish_is_served_with_the_time_from_postgres()
    {
        var dish = await Client.GetFromJsonAsync<DishOfTheDay>("/dish-of-the-day", Ct);

        Assert.NotNull(dish);
        Assert.Equal("Dal Baati Churma", dish.Dish);
        // The container's clock is not exactly ours, so compare with a tolerance.
        Assert.Equal(DateTime.UtcNow, dish.CookedAt.ToUniversalTime(), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Burnt_dish_returns_500()
    {
        using var response = await Client.GetAsync("/dish-of-the-day?burn=true", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // Guards Step 04, experiment 2: a misspelt ActivitySource name fails silently. Here it fails a test.
    [Fact]
    public async Task Request_joins_the_callers_trace_and_records_the_custom_span()
    {
        const string traceId = "0af7651916cd43dd8448eb211c80319c";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/dish-of-the-day");
        request.Headers.Add("traceparent", $"00-{traceId}-b7ad6b7169203331-01");

        using var response = await Client.SendAsync(request, Ct);
        // Check the status first: otherwise an error body fails later with a confusing null reference.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dish = await response.Content.ReadFromJsonAsync<DishOfTheDay>(Ct);

        Assert.NotNull(dish);
        Assert.Equal(traceId, dish.Trace.TraceId);
        Assert.NotNull(dish.Trace.CookSpanId);
    }

    [Fact]
    public async Task Chaos_makes_the_dish_fail_with_503_and_counts_it()
    {
        using var chaos = await Client.PostAsync("/chaos?failRate=1", content: null, Ct);
        using var response = await Client.GetAsync("/dish-of-the-day", Ct);
        var status = await Client.GetFromJsonAsync<ChaosStatus>("/chaos", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, status?.RequestsReceived);
        Assert.Equal(1, status?.FailuresInjected);
    }
}
