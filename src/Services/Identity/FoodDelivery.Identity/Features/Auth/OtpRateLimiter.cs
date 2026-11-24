using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FoodDelivery.Identity.Features.Auth;

/// <summary>The "OtpRateLimit" section: how many codes one email may request.</summary>
internal sealed class OtpRateLimitOptions
{
    public const string Section = "OtpRateLimit";

    public TimeSpan Gap { get; init; } = TimeSpan.FromSeconds(60);

    [Range(1, 1000)]
    public int PerHour { get; init; } = 5;

    [Range(1, 1000)]
    public int PerDay { get; init; } = 20;
}

/// <summary>
/// Per EMAIL and in Redis, so it holds across all instances and all attacking machines.
/// A sliding log: the sorted set "rl:otp:{email}" has one entry per accepted request, scored by its time.
/// </summary>
internal sealed class OtpRateLimiter(IConnectionMultiplexer redis, IOptions<OtpRateLimitOptions> options)
{
    // "At most N in a window" is the same as "the N-th newest request has left the window".
    // Redis's own clock is used, so instances with slightly different clocks agree.
    // Refused requests are not stored: trying again does not extend the wait.
    private const string Script = """
        local time = redis.call('TIME')
        local now = time[1] * 1000 + math.floor(time[2] / 1000)
        local hour = 3600000
        local day = 86400000
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - day)

        local function wait(window, limit)
          local entry = redis.call('ZREVRANGE', KEYS[1], limit - 1, limit - 1, 'WITHSCORES')
          if entry[2] == nil then return 0 end
          return math.max(0, tonumber(entry[2]) + window - now)
        end

        local ms = math.max(wait(tonumber(ARGV[1]), 1), wait(hour, tonumber(ARGV[2])), wait(day, tonumber(ARGV[3])))
        if ms > 0 then return ms end

        redis.call('ZADD', KEYS[1], now, ARGV[4])
        redis.call('PEXPIRE', KEYS[1], day)
        return 0
        """;

    private readonly OtpRateLimitOptions _options = options.Value;

    /// <summary>Null if a code may be sent now (and counts it), otherwise how long to wait.</summary>
    public async Task<TimeSpan?> TryAcquireAsync(string email)
    {
        var waitMs = (long)await redis.GetDatabase().ScriptEvaluateAsync(
            Script,
            [Key(email)],
            [(long)_options.Gap.TotalMilliseconds, _options.PerHour, _options.PerDay, Guid.NewGuid().ToString("N")]);

        return waitMs == 0 ? null : TimeSpan.FromMilliseconds(waitMs);
    }

    internal static RedisKey Key(string email) => $"rl:otp:{email}";
}
