using System.Security.Claims;

namespace FoodDelivery.Catalog.Features;

// A learning aid until Catalog has real endpoints (Step 18): what this service knows about the caller,
// all of it from the token alone. No call to Identity and no database.
internal static class WhoAmI
{
    internal sealed record Response(string? Id, string? Email, IReadOnlyList<string> Roles);

    // No RequireAuthorization(): the fallback policy protects every endpoint that doesn't opt out.
    public static void Map(IEndpointRouteBuilder app) => app.MapGet("/whoami", (ClaimsPrincipal user) => new Response(
        user.FindFirstValue("sub"),
        user.FindFirstValue("email"),
        user.FindAll("role").Select(claim => claim.Value).ToList()));
}
