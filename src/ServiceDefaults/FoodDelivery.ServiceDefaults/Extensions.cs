using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// Kept in the Microsoft.Extensions.Hosting namespace (the Aspire template's convention) so every
// service can call builder.AddServiceDefaults() without adding a using.
namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Cross-cutting wiring every service calls once at startup. It grows step by step:
/// Step 02 service discovery, Step 04 OpenTelemetry, Step 05 health checks + resilience,
/// Step 12 JWT validation.
/// </summary>
public static class Extensions
{
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        // Service discovery: turns a logical name ("kitchen") into real addresses.
        // The default provider reads configuration keys shaped like
        //   services:{serviceName}:{endpointName}:{index}
        // The AppHost supplies them as environment variables, e.g.
        //   services__kitchen__https__0 = https://localhost:7148
        builder.Services.AddServiceDiscovery();

        // Every HttpClient created through IHttpClientFactory gets the service discovery
        // handler, so a BaseAddress like "https+http://kitchen" is resolved on each request.
        builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());

        return builder;
    }

    /// <summary>
    /// Step 04: traces, metrics and logs, exported over OTLP.
    /// .NET code never talks to OpenTelemetry directly: it uses the BCL types
    /// ActivitySource (traces), Meter (metrics) and ILogger (logs). The OTel SDK
    /// registered here *listens* to the sources it is told about and exports what it hears.
    /// </summary>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        // Convention: a service names its own ActivitySource and Meter after its application
        // (assembly) name, e.g. "FoodDelivery.Sandbox.Kitchen". Only names subscribed here are
        // recorded; anything else is silently dropped (see the Step 04 experiments).
        var serviceSourceName = builder.Environment.ApplicationName;

        // Logs: ILogger output also goes to OTel. Each record carries the current TraceId/SpanId,
        // which is what lets the dashboard jump from a log line to its trace.
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()   // http.server.request.duration, active requests, ...
                .AddHttpClientInstrumentation()   // http.client.request.duration, open connections, ...
                .AddRuntimeInstrumentation()      // GC, heap size, thread pool, exceptions
                .AddMeter(serviceSourceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()   // one server span per incoming request
                .AddHttpClientInstrumentation()   // one client span per outgoing call (+ traceparent header)
                .AddSource(serviceSourceName));

        // The AppHost sets OTEL_EXPORTER_OTLP_ENDPOINT (plus headers and protocol) to point at the
        // Aspire dashboard. Without it (e.g. `dotnet run` outside the AppHost) nothing is exported.
        // UseOtlpExporter reads the standard OTEL_* environment variables for all three signals.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }
}
