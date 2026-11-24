using FoodDelivery.Identity.Data;
using FoodDelivery.Identity.Features.Tokens;
using FoodDelivery.Identity.Features.Users;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FoodDelivery.Identity.Features.Auth;

// Slice: "here is my code". Signing up and signing in are the same thing.
internal static class VerifyOtp
{
    internal sealed record Request(string? Email, string? Code);

    internal sealed record Response(string AccessToken, string TokenType, int ExpiresIn, UserInfo User);

    internal sealed record UserInfo(Guid Id, string Email, IReadOnlyList<string> Roles);

    public static void Map(IEndpointRouteBuilder app) => app.MapPost("/auth/otp/verify", HandleAsync)
        .RequireRateLimiting(IpRateLimiting.OtpVerifyPolicy);

    private static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        Request request, HttpContext http, OtpStore otps, IdentityDbContext db, TokenIssuer tokens, TimeProvider time, CancellationToken ct)
    {
        if (!EmailAddress.TryNormalize(request.Email, out var email)
            || request.Code is null
            || !await otps.VerifyAsync(email, request.Code.Trim()))
        {
            // One answer for wrong, expired, used up and never requested: no hints.
            return TypedResults.Problem("Invalid or expired code.", statusCode: StatusCodes.Status400BadRequest);
        }

        var user = await FindAsync(db, email, ct) ?? await CreateAsync(db, email, time, ct);

        // The token must not stay behind in a browser or proxy cache.
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new Response(
            AccessToken: tokens.Issue(user.Id, user.Email, user.Roles),
            TokenType: "Bearer",
            ExpiresIn: (int)tokens.Lifetime.TotalSeconds,
            User: user));
    }

    private static Task<UserInfo?> FindAsync(IdentityDbContext db, string email, CancellationToken ct) =>
        db.Users
            .Where(u => u.Email == email)
            .Select(u => new UserInfo(u.Id, u.Email, u.Roles.OrderBy(r => r.RoleId).Select(r => r.Role!.Name).ToList()))
            .SingleOrDefaultAsync(ct);

    private static async Task<UserInfo> CreateAsync(IdentityDbContext db, string email, TimeProvider time, CancellationToken ct)
    {
        var user = User.Create(email, RoleId.Customer, time.GetUtcNow());
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
            return new UserInfo(user.Id, user.Email, [nameof(RoleId.Customer)]);
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
