using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace FoodDelivery.Identity.Features.Auth;

/// <summary>The "IpRateLimit" section: requests per minute from one client address.</summary>
internal sealed class IpRateLimitOptions
{
    public const string Section = "IpRateLimit";

    [Range(1, int.MaxValue)]
    public int Global { get; init; } = 100;

    [Range(1, int.MaxValue)]
    public int OtpRequest { get; init; } = 10;

    [Range(1, int.MaxValue)]
    public int OtpVerify { get; init; } = 10;
}

/// <summary>
/// Per IP and in the memory of THIS instance: fast, no network call, but two instances allow twice as much.
/// It protects the server from one busy client. Accounts are protected by OtpRateLimiter.
/// </summary>
internal static class IpRateLimiting
{
    public const string OtpRequestPolicy = "otp-request";
    public const string OtpVerifyPolicy = "otp-verify";

    private static readonly TimeSpan _minute = TimeSpan.FromMinutes(1);

    public static void AddIpRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<IpRateLimitOptions>().BindConfiguration(IpRateLimitOptions.Section).ValidateDataAnnotations().ValidateOnStart();
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<IpRateLimitOptions>>((limiter, ipOptions) =>
        {
            var limits = ipOptions.Value;
            limiter.OnRejected = OnRejectedAsync;

            // FIXED WINDOW: simplest. Around the reset, up to twice the limit can get through.
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(ClientKey(context.Connection.RemoteIpAddress), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.Global,
                    Window = _minute,
                }));

            // SLIDING WINDOW: the minute moves in 6 steps of 10 s, so there is no moment when everything resets.
            limiter.AddPolicy(OtpRequestPolicy, context =>
                RateLimitPartition.GetSlidingWindowLimiter(ClientKey(context.Connection.RemoteIpAddress), _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = limits.OtpRequest,
                    Window = _minute,
                    SegmentsPerWindow = 6,
                }));

            // TOKEN BUCKET: a full bucket allows a burst (typing errors), then one token at a time.
            limiter.AddPolicy(OtpVerifyPolicy, context =>
                RateLimitPartition.GetTokenBucketLimiter(ClientKey(context.Connection.RemoteIpAddress), _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = limits.OtpVerify,
                    TokensPerPeriod = 1,
                    ReplenishmentPeriod = _minute / limits.OtpVerify,
                }));
        });
    }

    /// <summary>Who counts as one client.</summary>
    internal static string ClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        // One home connection usually owns a whole /64 (the first 8 bytes), so its addresses share a limit.
        var bytes = address.GetAddressBytes();
        bytes.AsSpan(8).Clear();
        return $"{new IPAddress(bytes)}/64";
    }

    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken ct)
    {
        TimeSpan? retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : null;
        await TooManyRequests(context.HttpContext, retryAfter).ExecuteAsync(context.HttpContext);
    }

    /// <summary>The 429 used by both kinds of limit.</summary>
    internal static IResult TooManyRequests(HttpContext context, TimeSpan? retryAfter)
    {
        if (retryAfter is { } wait)
        {
            // Whole seconds, rounded up: rounding down would invite a retry that is still too early.
            context.Response.Headers.RetryAfter = Math.Ceiling(wait.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        return TypedResults.Problem(title: "Too many requests. Please try again later.", statusCode: StatusCodes.Status429TooManyRequests);
    }
}
