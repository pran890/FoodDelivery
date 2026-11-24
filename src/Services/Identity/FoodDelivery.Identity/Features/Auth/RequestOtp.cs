using FoodDelivery.Identity.Features.Users;

namespace FoodDelivery.Identity.Features.Auth;

// Slice: "send me a code".
internal static partial class RequestOtp
{
    internal sealed record Request(string? Email);

    public static void Map(IEndpointRouteBuilder app) => app.MapPost("/auth/otp/request", HandleAsync)
        .RequireRateLimiting(IpRateLimiting.OtpRequestPolicy);

    private static async Task<IResult> HandleAsync(
        Request request, HttpContext http, OtpRateLimiter limiter, OtpStore otps, IOtpSender sender, ILogger<OtpStore> logger, CancellationToken ct)
    {
        // Depends only on the text of the email, so it says nothing about which accounts exist.
        if (!EmailAddress.TryNormalize(request.Email, out var email))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Not a valid email address."] });
        }

        // Known and unknown emails are counted alike, so a 429 says nothing about accounts either.
        // Before issuing: a refused request must not replace the code the user is typing in right now.
        if (await limiter.TryAcquireAsync(email) is { } wait)
        {
            return IpRateLimiting.TooManyRequests(http, wait);
        }

        // No database lookup: known and unknown emails take exactly the same path.
        var code = await otps.IssueAsync(email);

        try
        {
            await sender.SendAsync(email, code, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Still 202: an error here could depend on the address ("mailbox unknown").
            LogSendFailed(logger, ex);
        }

        return TypedResults.Accepted((string?)null);
    }

    // Neither the code nor the email is logged.
    [LoggerMessage(Level = LogLevel.Error, Message = "Sending a one-time code failed")]
    private static partial void LogSendFailed(ILogger logger, Exception exception);
}
