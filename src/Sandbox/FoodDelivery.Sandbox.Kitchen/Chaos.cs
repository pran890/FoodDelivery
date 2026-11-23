// Step 05: a tiny fault injector, so resilience can be tested on demand.
// SANDBOX ONLY. Real fault injection uses Polly's chaos strategies or a proxy (Toxiproxy).

internal sealed record ChaosSettings(double FailRate, int DelayMs);

internal sealed record ChaosStatus(ChaosSettings Settings, long RequestsReceived, long FailuresInjected);

/// <summary>
/// Shared by all requests (singleton). Settings are swapped as one immutable object, and the
/// counters use Interlocked, so concurrent requests never see a half-updated state.
/// </summary>
internal sealed class Chaos
{
    private ChaosSettings _settings = new(FailRate: 0, DelayMs: 0);
    private long _requestsReceived;
    private long _failuresInjected;

    public ChaosSettings Settings
    {
        get => Volatile.Read(ref _settings);
        set => Volatile.Write(ref _settings, value);
    }

    public ChaosStatus Status => new(Settings, Interlocked.Read(ref _requestsReceived), Interlocked.Read(ref _failuresInjected));

    /// <summary>Counts the request, applies the delay, and returns true if this request should fail.</summary>
    public async Task<bool> ApplyAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _requestsReceived);
        var settings = Settings;

        if (settings.DelayMs > 0)
        {
            // ct is HttpContext.RequestAborted: when the caller's attempt timeout fires,
            // the connection is dropped and this delay is cancelled too.
            await Task.Delay(settings.DelayMs, ct);
        }

        if (Random.Shared.NextDouble() < settings.FailRate)
        {
            Interlocked.Increment(ref _failuresInjected);
            return true;
        }

        return false;
    }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _requestsReceived, 0);
        Interlocked.Exchange(ref _failuresInjected, 0);
    }
}
