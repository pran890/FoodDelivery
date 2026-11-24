using FoodDelivery.Identity.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace FoodDelivery.Identity.Tests;

/// <summary>One Postgres container and one in-memory Identity, shared by all tests (see Step 06).</summary>
public sealed class IdentityFixture : IAsyncLifetime
{
    private const long Megabyte = 1024 * 1024;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgis/postgis:17-3.5")
        .WithCreateParameterModifier(p => p.HostConfig!.Memory = p.HostConfig.MemorySwap = 512 * Megabyte)
        .Build();

    private WebApplicationFactory<Program>? _factory;

    private WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("The fixture is not initialised yet.");

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        // Starting the app applies the migrations, so they are tested on every run.
        _factory = CreateFactory();
    }

    /// <summary>Another running Identity on the same database, like a second instance in production.</summary>
    public WebApplicationFactory<Program> CreateFactory(string environment = "Development") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("environment", environment);
            host.UseSetting("ConnectionStrings:identitydb", ConnectionString);
        });

    public string ConnectionString => _postgres.GetConnectionString();

    public HttpClient CreateClient() => Factory.CreateClient();

    public AsyncServiceScope CreateScope() => Factory.Services.CreateAsyncScope();

    /// <summary>Empties users and user_roles. The roles table is reference data and stays.</summary>
    public async Task ResetAsync()
    {
        await using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await db.Database.ExecuteSqlAsync($"TRUNCATE users CASCADE");
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class IdentitySuite : ICollectionFixture<IdentityFixture>
{
    public const string Name = "Identity";
}

[Collection(IdentitySuite.Name)]
public abstract class IdentityTest(IdentityFixture fixture) : IAsyncLifetime
{
    protected IdentityFixture Fixture { get; } = fixture;

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
