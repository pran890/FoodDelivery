using FoodDelivery.Identity.Data;
using FoodDelivery.Identity.Features.Users;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FoodDelivery.Identity.Features.Auth;

// Slice: "here is my code". Signing up and signing in are the same thing.
internal static class VerifyOtp
{
    internal sealed record Request(string? Email, string? Code);

    // Step 11 adds the access token.
    internal sealed record Response(Guid Id, string Email, IReadOnlyList<string> Roles);

    public static void Map(IEndpointRouteBuilder app) => app.MapPost("/auth/otp/verify", HandleAsync)
        .RequireRateLimiting(IpRateLimiting.OtpVerifyPolicy);

    private static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        Request request, OtpStore otps, IdentityDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (!EmailAddress.TryNormalize(request.Email, out var email)
            || request.Code is null
            || !await otps.VerifyAsync(email, request.Code.Trim()))
        {
            // One answer for wrong, expired, used up and never requested: no hints.
            return TypedResults.Problem("Invalid or expired code.", statusCode: StatusCodes.Status400BadRequest);
        }

        var user = await FindAsync(db, email, ct) ?? await CreateAsync(db, email, time, ct);
        return TypedResults.Ok(user);
    }

    private static Task<Response?> FindAsync(IdentityDbContext db, string email, CancellationToken ct) =>
        db.Users
            .Where(u => u.Email == email)
            .Select(u => new Response(u.Id, u.Email, u.Roles.OrderBy(r => r.RoleId).Select(r => r.Role!.Name).ToList()))
            .SingleOrDefaultAsync(ct);

    private static async Task<Response> CreateAsync(IdentityDbContext db, string email, TimeProvider time, CancellationToken ct)
    {
        var user = User.Create(email, RoleId.Customer, time.GetUtcNow());
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
            return new Response(user.Id, user.Email, [nameof(RoleId.Customer)]);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: IdentityDbContext.UniqueEmailIndex,
        })
        {
            // Somebody created this user after FindAsync looked. The code proved the email is theirs.
            return await FindAsync(db, email, ct) ?? throw new InvalidOperationException("The user vanished.");
        }
    }
}
