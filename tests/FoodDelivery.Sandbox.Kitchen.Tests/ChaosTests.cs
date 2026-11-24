namespace FoodDelivery.Sandbox.Kitchen.Tests;

// UNIT tests: one class, no I/O, no containers. xUnit creates a NEW ChaosTests instance for every
// test, so _chaos is never shared between tests and nothing can leak.
public class ChaosTests
{
    private readonly Chaos _chaos = new();

    // Pass the test's token so a cancelled run stops promptly (analyzer xUnit1051 insists on it).
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Random code is tested at the ends of its range, where the outcome is certain.
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task Fail_rate_decides_whether_a_request_fails(double failRate, bool expected)
    {
        _chaos.Settings = new ChaosSettings(failRate, DelayMs: 0);

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(expected, await _chaos.ApplyAsync(Ct));
        }
    }

    [Fact]
    public async Task Counters_count_requests_and_failures_until_reset()
    {
        _chaos.Settings = new ChaosSettings(FailRate: 1, DelayMs: 0);
        await _chaos.ApplyAsync(Ct);
        await _chaos.ApplyAsync(Ct);

        Assert.Equal(2, _chaos.Status.RequestsReceived);
        Assert.Equal(2, _chaos.Status.FailuresInjected);

        _chaos.ResetCounters();

        Assert.Equal(0, _chaos.Status.RequestsReceived);
        Assert.Equal(0, _chaos.Status.FailuresInjected);
    }

    [Fact]
    public async Task Delay_stops_when_the_caller_gives_up()
    {
        _chaos.Settings = new ChaosSettings(FailRate: 0, DelayMs: 60_000);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _chaos.ApplyAsync(caller.Token));
    }
}
