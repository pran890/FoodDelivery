using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace FoodDelivery.Sandbox.Kitchen.Tests;

/// <summary>
/// The expensive things, created ONCE and shared: two throwaway containers and one running Kitchen.
/// Shared means state survives from one test to the next, so every test resets it first.
/// </summary>
public sealed class KitchenFixture : IAsyncLifetime
{
    private const long Megabyte = 1024 * 1024;

    // Same images and memory caps as the AppHost, so tests meet the same Postgres and Redis.
    // Testcontainers picks random free host ports and uses no named volumes: development data is never touched.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgis/postgis:17-3.5")
        .WithCreateParameterModifier(p => p.HostConfig!.Memory = p.HostConfig.MemorySwap = 512 * Megabyte)
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:8.6")
        .WithCreateParameterModifier(p => p.HostConfig!.Memory = p.HostConfig.MemorySwap = 128 * Megabyte)
        .Build();

    private WebApplicationFactory<Program>? _factory;

    private WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("The fixture is not initialised yet.");

    public string RedisConnectionString => _redis.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        _factory = CreateFactory(_postgres.GetConnectionString(), RedisConnectionString);
    }

    /// <summary>Starts the real Kitchen in memory: real routing, DI and middleware, no network port.</summary>
    public static WebApplicationFactory<Program> CreateFactory(string postgres, string redis) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            // UseSetting, not ConfigureAppConfiguration: the Kitchen reads its connection strings
            // BEFORE builder.Build(), and only settings passed this way are visible that early.
            host.UseSetting("ConnectionStrings:sandboxdb", postgres);
            host.UseSetting("ConnectionStrings:redis", redis);
        });

    public HttpClient CreateClient() => Factory.CreateClient();

    /// <summary>Puts everything the tests share back to a known state.</summary>
    public async Task ResetAsync()
    {
        // Shared state 1: data in Redis. (The Kitchen's own connection allows FLUSHDB: AllowAdmin.)
        var redis = Factory.Services.GetRequiredService<IConnectionMultiplexer>();
        await redis.GetServer(redis.GetEndPoints()[0]).FlushDatabaseAsync();

        // Shared state 2: singletons inside the running app. Easy to forget.
        var chaos = Factory.Services.GetRequiredService<Chaos>();
        chaos.Settings = new ChaosSettings(FailRate: 0, DelayMs: 0);
        chaos.ResetCounters();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _redis.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

/// <summary>
/// Test classes in one collection share one fixture AND run one after another.
/// Classes in different collections run in parallel, which would race on the shared Redis.
/// </summary>
[CollectionDefinition(Name)]
public sealed class KitchenSuite : ICollectionFixture<KitchenFixture>
{
    public const string Name = "Kitchen";
}

/// <summary>Base class: xUnit calls InitializeAsync before EVERY test.</summary>
[Collection(KitchenSuite.Name)]
public abstract class KitchenTest(KitchenFixture fixture) : IAsyncLifetime
{
    protected KitchenFixture Fixture { get; } = fixture;

    protected HttpClient Client { get; } = fixture.CreateClient();

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await Fixture.ResetAsync();

    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
