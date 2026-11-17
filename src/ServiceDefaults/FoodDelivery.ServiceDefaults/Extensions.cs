using Microsoft.Extensions.DependencyInjection;

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
}
