// SANDBOX: a throwaway API for learning steps 02-05. Deleted once real services exist.
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// A typed HttpClient. "https+http://kitchen" means: look up the service named "kitchen",
// prefer its https endpoint, fall back to http. No host or port appears in code.
builder.Services.AddHttpClient<KitchenClient>(client => client.BaseAddress = new Uri("https+http://kitchen"));

var app = builder.Build();

// Step 04: no tracing code here. ASP.NET Core starts a server span for this request and
// HttpClient starts a client span for the kitchen call and adds the W3C `traceparent` header,
// so the kitchen continues the SAME trace. ?burn=true is passed on to see an error cross services.
app.MapGet("/order", async (KitchenClient kitchen, CancellationToken ct, bool burn = false) =>
{
    var dish = await kitchen.GetDishOfTheDayAsync(burn, ct);
    return new OrderResponse($"Your {dish.Dish} is on its way.", dish, WaiterTraceId: Activity.Current?.TraceId.ToString());
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
    public async Task<DishOfTheDay> GetDishOfTheDayAsync(bool burn, CancellationToken ct) =>
        await http.GetFromJsonAsync<DishOfTheDay>(burn ? "/dish-of-the-day?burn=true" : "/dish-of-the-day", ct)
        ?? throw new InvalidOperationException("Kitchen returned an empty response.");
}

internal sealed record DishOfTheDay(string Dish, string CookedBy, string ListeningOn, TraceInfo Trace);

internal sealed record TraceInfo(string? ReceivedTraceparent, string? TraceId, string? CookSpanId);

// Same trace id in WaiterTraceId and FromKitchen.Trace.TraceId = propagation works.
internal sealed record OrderResponse(string Message, DishOfTheDay FromKitchen, string? WaiterTraceId);
