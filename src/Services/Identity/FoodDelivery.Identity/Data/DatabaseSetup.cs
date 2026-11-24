using FoodDelivery.Identity.Features.Users;
using Microsoft.EntityFrameworkCore;

namespace FoodDelivery.Identity.Data;

internal static class DatabaseSetup
{
    /// <summary>
    /// DEVELOPMENT ONLY. In production a deployment step applies migrations (a reviewed SQL script),
    /// so the app itself needs no right to change tables.
    /// </summary>
    public static async Task MigrateAndSeedAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        await db.Database.MigrateAsync();

        var adminEmail = app.Configuration["Identity:AdminEmail"]
            ?? throw new InvalidOperationException("Missing Identity:AdminEmail.");
        await SeedAdminAsync(db, adminEmail, app.Services.GetRequiredService<TimeProvider>());
    }

    /// <summary>
    /// The admin differs per environment, so it comes from configuration and not from a migration.
    /// ON CONFLICT DO NOTHING makes this safe to repeat, and safe when two instances start together
    /// ("check, then insert" would not be).
    /// </summary>
    public static async Task SeedAdminAsync(IdentityDbContext db, string adminEmail, TimeProvider time)
    {
        if (!EmailAddress.TryNormalize(adminEmail, out var email))
        {
            throw new InvalidOperationException("Identity:AdminEmail is not a valid email address.");
        }

        var admin = User.Create(email, RoleId.Admin, time.GetUtcNow());
        var adminRole = (short)RoleId.Admin;

        // An interpolated string here becomes SQL PARAMETERS, not text, so it can't be injected.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO users (id, email, created_at)
            VALUES ({admin.Id}, {admin.Email}, {admin.CreatedAt})
            ON CONFLICT (email) DO NOTHING;

            INSERT INTO user_roles (user_id, role_id, granted_at)
            SELECT id, {adminRole}, {admin.CreatedAt} FROM users WHERE email = {admin.Email}
            ON CONFLICT DO NOTHING;
            """);
    }
}
