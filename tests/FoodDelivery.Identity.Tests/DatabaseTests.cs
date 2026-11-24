using System.Net;
using FoodDelivery.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FoodDelivery.Identity.Tests;

public sealed class DatabaseTests(IdentityFixture fixture) : IdentityTest(fixture)
{
    // Fails when someone changes the model and forgets the migration.
    [Fact]
    public async Task Every_model_change_has_a_migration()
    {
        await using var scope = Fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        Assert.False(db.Database.HasPendingModelChanges());
    }

    // Stored ids must keep their meaning, so this compares the table with the enum.
    [Fact]
    public async Task Roles_table_matches_the_enum()
    {
        await using var scope = Fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var rows = await db.Set<Role>().OrderBy(r => r.Id).Select(r => r.Name).ToListAsync(Ct);

        Assert.Equal(["Customer", "RestaurantOwner", "Rider", "Admin"], rows);
        Assert.Equal(rows, Enum.GetNames<RoleId>());
    }

    [Fact]
    public async Task Seeding_twice_leaves_one_admin()
    {
        await using var scope = Fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        await DatabaseSetup.SeedAdminAsync(db, "Admin@Example.com", TimeProvider.System);
        await DatabaseSetup.SeedAdminAsync(db, "admin@example.com", TimeProvider.System);

        var admin = await db.Users.Include(u => u.Roles).SingleAsync(Ct);
        Assert.Equal("admin@example.com", admin.Email);
        Assert.Equal([RoleId.Admin], admin.Roles.Select(r => r.RoleId));
    }

    // The last line of defence: even SQL written by hand can't store a mixed-case email.
    [Fact]
    public async Task Database_rejects_a_mixed_case_email()
    {
        await using var scope = Fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO users (id, email, created_at) VALUES ({Guid.CreateVersion7()}, 'Asha@example.com', now())", Ct));

        Assert.Equal("ck_users_email_lowercase", error.ConstraintName);
    }

    // Step 05's most important rule: a dead database fails readiness, never liveness (a restart fixes nothing).
    [Fact]
    public async Task Dead_database_fails_readiness_but_not_liveness()
    {
        // An Identity with its own database, which is dropped after the start.
        var connectionString = new NpgsqlConnectionStringBuilder(Fixture.ConnectionString) { Database = "doomed" }.ConnectionString;
        await using var factory = Fixture.CreateFactory(("ConnectionStrings:identitydb", connectionString));
        using var client = factory.CreateClient();
        await using var admin = new NpgsqlConnection(Fixture.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var drop = new NpgsqlCommand("DROP DATABASE doomed WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync(Ct);

        using var health = await client.GetAsync("/health", Ct);
        using var alive = await client.GetAsync("/alive", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
    }

    [Fact]
    public async Task Identity_is_healthy_when_its_database_is_up()
    {
        using var response = await Client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
