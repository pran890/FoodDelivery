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

    [Fact]
    public async Task Identity_is_healthy_when_its_database_is_up()
    {
        using var response = await Client.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
