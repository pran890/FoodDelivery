using FoodDelivery.Identity.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FoodDelivery.Identity.Features.Users;

// A SLICE: endpoint, request, response and logic of one use case in one file.
// DEVELOPMENT ONLY until Step 09, where verifying an email OTP creates the user.
internal static class CreateUser
{
    internal sealed record Request(string? Email);

    internal sealed record Response(Guid Id, string Email, DateTimeOffset CreatedAt, IReadOnlyList<string> Roles);

    public static void Map(IEndpointRouteBuilder app) => app.MapPost("/users", HandleAsync);

    private static async Task<Results<Created<Response>, ValidationProblem, Conflict<ProblemDetails>>> HandleAsync(
        Request request, IdentityDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (!EmailAddress.TryNormalize(request.Email, out var email))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Not a valid email address."] });
        }

        var user = User.Create(email, RoleId.Customer, time.GetUtcNow());
        db.Users.Add(user);

        try
        {
            // Both INSERTs (users and user_roles) run in one transaction.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: IdentityDbContext.UniqueEmailIndex,
        })
        {
            // No "does it exist?" query first: two requests could both see "no". The index decides.
            return TypedResults.Conflict(new ProblemDetails { Title = "This email is already registered.", Status = StatusCodes.Status409Conflict });
        }

        var response = new Response(user.Id, user.Email, user.CreatedAt, [nameof(RoleId.Customer)]);
        return TypedResults.Created($"/users/{user.Id}", response);
    }
}
