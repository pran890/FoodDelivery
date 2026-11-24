using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FoodDelivery.Identity.Data;

internal sealed class IdentityDbHealthCheck(IdentityDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Cannot connect to identitydb.");
}
