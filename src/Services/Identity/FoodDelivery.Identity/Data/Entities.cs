namespace FoodDelivery.Identity.Data;

internal sealed class User
{
    public required Guid Id { get; init; }

    /// <summary>Always normalised with <see cref="Features.Users.EmailAddress"/>.</summary>
    public required string Email { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public List<UserRole> Roles { get; init; } = [];

    public static User Create(string normalizedEmail, RoleId role, DateTimeOffset now)
    {
        // .NET keeps 100 ns ticks, Postgres timestamptz keeps 1 µs. Round now, so the value we
        // return after saving equals the value a later read returns.
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));

        // UUIDv7 starts with a timestamp, so new keys land at the end of the index instead of at
        // random pages, and unlike an incrementing number they can't be guessed.
        var id = Guid.CreateVersion7(now);
        return new User
        {
            Id = id,
            Email = normalizedEmail,
            CreatedAt = now,
            Roles = [new UserRole { UserId = id, RoleId = role, GrantedAt = now }],
        };
    }
}

/// <summary>
/// The fixed ids of the rows in the roles table. Never renumber: they are stored in user_roles.
/// </summary>
internal enum RoleId : short
{
    Customer = 1,
    RestaurantOwner = 2,
    Rider = 3,
    Admin = 4,
}

internal sealed class Role
{
    public required RoleId Id { get; init; }

    public required string Name { get; init; }
}

internal sealed class UserRole
{
    public required Guid UserId { get; init; }

    public required RoleId RoleId { get; init; }

    public required DateTimeOffset GrantedAt { get; init; }

    public Role? Role { get; init; }
}
