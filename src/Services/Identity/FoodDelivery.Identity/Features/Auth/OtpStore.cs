using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FoodDelivery.Identity.Features.Auth;

/// <summary>
/// One-time codes in Redis: a hash "otp:{email}" with the fields "mac" and "attempts", and an expiry.
/// The code itself is never stored. Emails must already be normalised.
/// </summary>
internal sealed class OtpStore(IConnectionMultiplexer redis, IOptions<OtpOptions> options)
{
    private const string MacField = "mac";
    private const string AttemptsField = "attempts";

    // A Lua script runs as one step: no other command can run in between.
    // EXISTS first, because HINCRBY on an expired key would create a new one that never expires.
    private const string TakeAttemptScript = """
        if redis.call('EXISTS', KEYS[1]) == 0 then return false end
        local attempts = redis.call('HINCRBY', KEYS[1], 'attempts', 1)
        if attempts > tonumber(ARGV[1]) then return false end
        return { redis.call('HGET', KEYS[1], 'mac'), attempts }
        """;

    // Deletes only the code we compared with: not one that was requested in the meantime, and not twice.
    private const string ConsumeScript = """
        if redis.call('HGET', KEYS[1], 'mac') == ARGV[1] then return redis.call('DEL', KEYS[1]) end
        return 0
        """;

    private readonly OtpOptions _options = options.Value;
    private readonly byte[] _hmacKey = Encoding.UTF8.GetBytes(options.Value.HmacKey);

    private IDatabase Db => redis.GetDatabase();

    /// <summary>Creates a code for this email, replacing an earlier one and its attempt count.</summary>
    public async Task<string> IssueAsync(string email)
    {
        // Cryptographically random. System.Random is predictable from a few earlier values.
        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);

        // MULTI/EXEC: the fields and the expiry arrive together, so a code can't exist without one.
        var transaction = Db.CreateTransaction();
        _ = transaction.HashSetAsync(Key(email), [new(MacField, Mac(email, code)), new(AttemptsField, 0)]);
        _ = transaction.KeyExpireAsync(Key(email), _options.Lifetime);
        await transaction.ExecuteAsync();

        return code;
    }

    /// <summary>True at most once per code, and only within the allowed attempts.</summary>
    public async Task<bool> VerifyAsync(string email, string code)
    {
        if (await TakeAttemptAsync(email) is not { } attempt)
        {
            return false;
        }

        // Takes the same time wherever the first difference is, so timing tells nothing.
        var correct = CryptographicOperations.FixedTimeEquals(attempt.Mac, Mac(email, code));

        if (correct || attempt.Number >= _options.MaxAttempts)
        {
            var deleted = (long)await Db.ScriptEvaluateAsync(ConsumeScript, [Key(email)], [attempt.Mac]) == 1;
            // Two correct requests at once: both compare equal, but only one of them deletes the key.
            return correct && deleted;
        }

        return false;
    }

    /// <summary>
    /// Counts the attempt BEFORE comparing. "Compare, then count" would let many simultaneous guesses
    /// all see a low count.
    /// </summary>
    internal async Task<(byte[] Mac, int Number)?> TakeAttemptAsync(string email)
    {
        var result = await Db.ScriptEvaluateAsync(TakeAttemptScript, [Key(email)], [_options.MaxAttempts]);
        return result.IsNull ? null : ((byte[])result[0]!, (int)result[1]);
    }

    internal static RedisKey Key(string email) => $"otp:{email}";

    /// <summary>
    /// An HMAC and not a plain hash: there are only a million codes, so a plain hash can be reversed by
    /// trying them all. The email is included, so a stored value is worthless under another email.
    /// </summary>
    private byte[] Mac(string email, string code) =>
        HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes($"otp:v1:{email}:{code}"));
}
