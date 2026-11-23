using System.Diagnostics;
using System.Diagnostics.Metrics;

// Step 04: custom instrumentation. Only System.Diagnostics types here: no OpenTelemetry package.
// The OTel SDK in ServiceDefaults subscribes to these by name and exports them.

internal static class KitchenTelemetry
{
    // MUST equal the application (assembly) name: ServiceDefaults subscribes to exactly that name
    // with AddSource(...) / AddMeter(...). A typo here means silent data loss, not an error.
    public const string SourceName = "FoodDelivery.Sandbox.Kitchen";

    // One ActivitySource per component, created once and kept for the process lifetime.
    // StartActivity returns null when nobody is listening, so callers use activity?.X.
    public static readonly ActivitySource ActivitySource = new(SourceName);
}

/// <summary>
/// Business metrics. Created through IMeterFactory (the .NET 8+ way) so the Meter is owned by DI
/// and tests can create isolated instances.
/// </summary>
internal sealed class KitchenMetrics
{
    private readonly Counter<long> _dishesCooked;
    private readonly Histogram<double> _cookDuration;

    public KitchenMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(KitchenTelemetry.SourceName);

        // Counter: only goes up. The backend turns it into a rate ("dishes per minute").
        _dishesCooked = meter.CreateCounter<long>(
            "kitchen.dishes.cooked", unit: "{dish}", description: "Dishes finished by the kitchen.");

        // Histogram: a distribution, so you can ask for p50/p95/p99, not just an average.
        // OTel semantic conventions use seconds for durations.
        _cookDuration = meter.CreateHistogram<double>(
            "kitchen.dish.cook.duration", unit: "s", description: "Time spent cooking one dish.");
    }

    public void DishCooked(string dish, bool burnt, TimeSpan duration)
    {
        // Tags (dimensions) must have LOW cardinality: a handful of dish names is fine,
        // an order id or user id here would create one time series per value.
        var tags = new TagList
        {
            { "kitchen.dish.name", dish },
            { "kitchen.dish.burnt", burnt },
        };
        _dishesCooked.Add(1, tags);
        _cookDuration.Record(duration.TotalSeconds, tags);
    }
}

/// <summary>
/// Source-generated log methods: no boxing or message parsing when the level is disabled,
/// and the named placeholders become structured attributes on the OTel log record.
/// </summary>
internal static partial class KitchenLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Cooked {Dish} in {ElapsedMs} ms")]
    public static partial void DishCooked(this ILogger logger, string dish, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Burnt {Dish} after {ElapsedMs} ms")]
    public static partial void DishBurnt(this ILogger logger, string dish, long elapsedMs);
}
