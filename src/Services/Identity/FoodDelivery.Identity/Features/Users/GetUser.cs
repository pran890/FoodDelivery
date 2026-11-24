using FoodDelivery.Identity.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace FoodDelivery.Identity.Features.Users;

// DEVELOPMENT ONLY: it returns an email address, and nothing protects it until tokens exist (Step 11).
internal static class GetUser
{
    // The same shape as CreateUser's response today, but deliberately not shared: slices change
    // independently, and a shared type would tie them together.
    internal sealed record Response(Guid Id, string Email, DateTimeOffset CreatedAt, IReadOnlyList<string> Roles);

    public static void Map(IEndpointRouteBuilder app) => app.MapGet("/users/{id:guid}", HandleAsync);

    private static async Task<Results<Ok<Response>, NotFound>> HandleAsync(Guid id, IdentityDbContext db, CancellationToken ct)
    {
        // Projecting into a record means EF selects only these columns and tracks nothing.
        var user = await db.Users
            .Where(u => u.Id == id)
            .Select(u => new Response(
                u.Id,
                u.Email,
                u.CreatedAt,
                u.Roles.OrderBy(r => r.RoleId).Select(r => r.Role!.Name).ToList()))
            .SingleOrDefaultAsync(ct);

        return user is null ? TypedResults.NotFound() : TypedResults.Ok(user);
    }
}
