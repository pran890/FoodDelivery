using Microsoft.EntityFrameworkCore;

namespace FoodDelivery.Identity.Data;

/// <summary>
/// One DbContext = one unit of work: it tracks the objects it loads and turns the changes into SQL
/// in SaveChanges. It's created per request (scoped) and is not thread-safe.
/// Names become snake_case through UseSnakeCaseNamingConvention (see Program.cs).
/// </summary>
internal sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public const string UniqueEmailIndex = "ux_users_email";

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            // The database enforces the rules too, so a bug or a manual INSERT can't break them.
            user.ToTable("users", t => t.HasCheckConstraint("ck_users_email_lowercase", "email = lower(email)"));
            user.Property(u => u.Id).ValueGeneratedNever();
            user.Property(u => u.Email).HasMaxLength(254);
            user.HasIndex(u => u.Email).IsUnique().HasDatabaseName(UniqueEmailIndex);
        });

        modelBuilder.Entity<Role>(role =>
        {
            role.ToTable("roles");
            role.Property(r => r.Id).ValueGeneratedNever();
            role.Property(r => r.Name).HasMaxLength(32);
            role.HasIndex(r => r.Name).IsUnique().HasDatabaseName("ux_roles_name");
            // Reference data: the same in every environment, so it's part of the migrations.
            role.HasData(Enum.GetValues<RoleId>().Select(id => new Role { Id = id, Name = id.ToString() }));
        });

        modelBuilder.Entity<UserRole>(userRole =>
        {
            userRole.ToTable("user_roles");
            userRole.HasKey(ur => new { ur.UserId, ur.RoleId });
            userRole.HasOne<User>().WithMany(u => u.Roles).HasForeignKey(ur => ur.UserId).OnDelete(DeleteBehavior.Cascade);
            userRole.HasOne(ur => ur.Role).WithMany().HasForeignKey(ur => ur.RoleId).OnDelete(DeleteBehavior.Restrict);
            // The primary key starts with user_id, so it can't answer "who has role X?" efficiently.
            userRole.HasIndex(ur => ur.RoleId).HasDatabaseName("ix_user_roles_role_id");
        });
    }
}
