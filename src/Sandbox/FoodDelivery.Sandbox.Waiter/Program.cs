// SANDBOX: a throwaway API for learning steps 02-05. Deleted once real services exist.
using System.Diagnostics;
using Microsoft.Extensions.Http.Resilience;
using Polly.CircuitBreaker;
using Polly.Timeout;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// A typed HttpClient. "https+http://kitchen" means: look up the service named "kitchen",
// prefer its https endpoint, fall back to http. No host or port appears in code.
// Step 05: it inherits the standard resilience pipeline from ServiceDefaults; no code needed here.
builder.Services.AddHttpClient<KitchenClient>(client => client.BaseAddress = new Uri("https+http://kitchen"));

// Step 05, SANDBOX DEMO VALUES: the defaults are sized for production traffic (the breaker needs
// ≥100 requests in 30 s; retries wait 2 s, 4 s, 8 s). Smaller numbers make the behaviour visible
// with a handful of curl calls. ConfigureAll = every named pipeline, including "KitchenClient-standard".
builder.Services.ConfigureAll<HttpStandardResilienceOptions>(options =>
{
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(8);      // default 30 s
    options.Retry.Delay = TimeSpan.FromMilliseconds(200);               // default 2 s (then ×2 each retry, ± jitter)
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);           // default 10 s
    options.CircuitBreaker.MinimumThroughput = 10;                      // default 100
    options.CircuitBreaker.FailureRatio = 0.5;                          // default 0.1
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(10); // default 30 s; must be ≥ 2 × attempt timeout
    options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(10);    // default 5 s
});

var app = builder.Build();
app.MapDefaultEndpoints();

// Step 04: no tracing code here. ASP.NET Core starts a server span for this request and
// HttpClient starts a client span for the kitchen call and adds the W3C `traceparent` header,
// so the kitchen continues the SAME trace. ?burn=true is passed on to see an error cross services.
// Step 05: when the pipeline finally gives up, say WHICH strategy gave up (learning aid).
app.MapGet("/order", async (KitchenClient kitchen, CancellationToken ct, bool burn = false) =>
{
    try
    {
        var dish = await kitchen.GetDishOfTheDayAsync(burn, ct);
        return Results.Ok(new OrderResponse($"Your {dish.Dish} is on its way.", dish, WaiterTraceId: Activity.Current?.TraceId.ToString()));
    }
    catch (BrokenCircuitException ex)
    {
        // The breaker is open: the kitchen was NOT called at all. Fails in ~1 ms.
        return KitchenUnavailable("circuit-open", ex);
    }
    catch (TimeoutRejectedException ex)
    {
        // The total timeout (or a lone attempt timeout with no retries left) gave up.
        return KitchenUnavailable("timeout", ex);
    }
    catch (HttpRequestException ex)
    {
        // Retries exhausted: the last attempt still got a 5xx, or couldn't connect.
        return KitchenUnavailable("retries-exhausted", ex);
    }
});

// Learning aid: shows the raw configuration that service discovery reads.
app.MapGet("/debug/discovery", (IConfiguration config) =>
    config.GetSection("services").AsEnumerable()
        .Where(kv => kv.Value is not null)
        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
        .ToDictionary(kv => kv.Key, kv => kv.Value));

app.Run();

static IResult KitchenUnavailable(string reason, Exception ex) =>
    Results.Problem(
        title: "The kitchen is unavailable.",
        detail: $"{ex.GetType().Name}: {ex.Message}",
        statusCode: StatusCodes.Status503ServiceUnavailable,
        extensions: new Dictionary<string, object?> { ["reason"] = reason });

internal sealed class KitchenClient(HttpClient http)
{
    public async Task<DishOfTheDay> GetDishOfTheDayAsync(bool burn, CancellationToken ct) =>
        await http.GetFromJsonAsync<DishOfTheDay>(burn ? "/dish-of-the-day?burn=true" : "/dish-of-the-day", ct)
        ?? throw new InvalidOperationException("Kitchen returned an empty response.");
}

internal sealed record DishOfTheDay(string Dish, string CookedBy, string ListeningOn, DateTime CookedAt, TraceInfo Trace);

internal sealed record TraceInfo(string? ReceivedTraceparent, string? TraceId, string? CookSpanId);

// Same trace id in WaiterTraceId and FromKitchen.Trace.TraceId = propagation works.
internal sealed record OrderResponse(string Message, DishOfTheDay FromKitchen, string? WaiterTraceId);
