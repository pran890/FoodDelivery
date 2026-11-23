using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using StackExchange.Redis;

// Step 05: readiness checks, hand-written so the mechanism is visible. (The AspNetCore.HealthChecks.*
// packages provide ready-made ones.) If a check throws, the health check service reports it with the
// registration's failureStatus (Unhealthy by default), so there's no try/catch here.

/// <summary>Can we open a connection and run a trivial query?</summary>
internal sealed class PostgresHealthCheck(NpgsqlDataSource db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var cmd = db.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync(cancellationToken);
        return HealthCheckResult.Healthy("SELECT 1 ok");
    }
}

/// <summary>Does Redis answer PING?</summary>
internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var latency = await redis.GetDatabase().PingAsync();
        return HealthCheckResult.Healthy($"PING {latency.TotalMilliseconds:F1} ms");
    }
}
