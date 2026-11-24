using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace FoodDelivery.Identity.Features.Auth;

internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await redis.GetDatabase().PingAsync();
        return HealthCheckResult.Healthy();
    }
}

/// <summary>Redis down, too slow or full: 503 "try again later" instead of a 500 that looks like a bug.</summary>
internal sealed class RedisExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not (RedisException or RedisTimeoutException))
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = { Title = "Sign-in is temporarily unavailable.", Status = StatusCodes.Status503ServiceUnavailable },
        });
    }
}
