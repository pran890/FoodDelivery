using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
        builder.AddDefaultHealthChecks();

        // Service discovery: turns a logical name ("kitchen") into real addresses.
        // The default provider reads configuration keys shaped like
        //   services:{serviceName}:{endpointName}:{index}
        // The AppHost supplies them as environment variables, e.g.
        //   services__kitchen__https__0 = https://localhost:7148
        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Step 05: every HttpClient from IHttpClientFactory gets the standard resilience
            // pipeline (outermost first): rate limiter → total timeout → retry → circuit breaker
            // → attempt timeout. Defaults: 30 s total, 3 retries (exponential backoff from 2 s,
            // with jitter), breaker opens at ≥10% failures over 30 s with ≥100 requests, 10 s per attempt.
            http.AddStandardResilienceHandler();

            // Registered AFTER resilience, so it sits INSIDE the retry: each attempt resolves the
            // address again ("https+http://kitchen"), and a retry can go to another instance.
            http.AddServiceDiscovery();
        });

        return builder;
    }

    /// <summary>
    /// Step 05: one check every service has. "self" answers only "is this process able to respond?"
    /// and is tagged "live". Services add their dependency checks (database, cache) WITHOUT that tag.
    /// </summary>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [LiveTag]);

        return builder;
    }

    /// <summary>
    /// Step 05: the two probe endpoints. Call once in every service, after builder.Build().
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Health details (which dependency is down, error messages) help an attacker map your
        // system, so they're only exposed in Development. In production they'd be on an internal
        // port or network that only the orchestrator reaches.
        if (app.Environment.IsDevelopment())
        {
            // Readiness: ALL checks (self + dependencies). Unhealthy → "don't send me traffic".
            app.MapHealthChecks(ReadinessPath, new HealthCheckOptions { ResponseWriter = WriteHealthJson });

            // Liveness: only "live" checks. Unhealthy → "restart me". Never include dependencies
            // here: a database outage would restart every service, which fixes nothing.
            app.MapHealthChecks(LivenessPath, new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains(LiveTag),
                ResponseWriter = WriteHealthJson,
            });
        }

        return app;
    }

    private const string LiveTag = "live";
    private const string ReadinessPath = "/health";
    private const string LivenessPath = "/alive";

    // The built-in writer prints only "Healthy"/"Unhealthy". This one shows each check, for learning.
    // The HTTP status is set by the middleware: 200 for Healthy/Degraded, 503 for Unhealthy.
    private static Task WriteHealthJson(HttpContext context, HealthReport report) =>
        context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            totalMs = (long)report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = (long)e.Value.Duration.TotalMilliseconds,
                description = e.Value.Description,
                error = e.Value.Exception?.Message,
            }),
        });

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
                // One server span per incoming request, except health probes (Step 05): the
                // orchestrator calls them every few seconds and they'd bury the real traces.
                .AddAspNetCoreInstrumentation(options => options.Filter = context =>
                    !context.Request.Path.StartsWithSegments(ReadinessPath)
                    && !context.Request.Path.StartsWithSegments(LivenessPath))
                .AddHttpClientInstrumentation()   // one client span per outgoing call (+ traceparent header)
                .AddSource(serviceSourceName)
                .AddSource("Npgsql"));            // Step 08: one span per SQL command (only a name, no package)

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
