using FoodDelivery.Identity.Features.Users;
using Microsoft.AspNetCore.Http.HttpResults;

namespace FoodDelivery.Identity.Features.Auth;

// Slice: "send me a code".
internal static partial class RequestOtp
{
    internal sealed record Request(string? Email);

    public static void Map(IEndpointRouteBuilder app) => app.MapPost("/auth/otp/request", HandleAsync);

    private static async Task<Results<Accepted, ValidationProblem>> HandleAsync(
        Request request, OtpStore otps, IOtpSender sender, ILogger<OtpStore> logger, CancellationToken ct)
    {
        // Depends only on the text of the email, so it says nothing about which accounts exist.
        if (!EmailAddress.TryNormalize(request.Email, out var email))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Not a valid email address."] });
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
