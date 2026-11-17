// SANDBOX: a throwaway API for learning steps 02-05. Deleted once real services exist.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// A typed HttpClient. "https+http://kitchen" means: look up the service named "kitchen",
// prefer its https endpoint, fall back to http. No host or port appears in code.
builder.Services.AddHttpClient<KitchenClient>(client => client.BaseAddress = new Uri("https+http://kitchen"));

var app = builder.Build();

app.MapGet("/order", async (KitchenClient kitchen, CancellationToken ct) =>
{
    var dish = await kitchen.GetDishOfTheDayAsync(ct);
    return new OrderResponse($"Your {dish.Dish} is on its way.", dish);
});

// Learning aid: shows the raw configuration that service discovery reads.
app.MapGet("/debug/discovery", (IConfiguration config) =>
    config.GetSection("services").AsEnumerable()
        .Where(kv => kv.Value is not null)
        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
        .ToDictionary(kv => kv.Key, kv => kv.Value));

app.Run();

internal sealed class KitchenClient(HttpClient http)
{
    public async Task<DishOfTheDay> GetDishOfTheDayAsync(CancellationToken ct) =>
        await http.GetFromJsonAsync<DishOfTheDay>("/dish-of-the-day", ct)
        ?? throw new InvalidOperationException("Kitchen returned an empty response.");
}

internal sealed record DishOfTheDay(string Dish, string CookedBy, string ListeningOn);

internal sealed record OrderResponse(string Message, DishOfTheDay FromKitchen);
