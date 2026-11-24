using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using FoodDelivery.Identity.Data;
using FoodDelivery.Identity.Features.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace FoodDelivery.Identity.Tests;

/// <summary>Postgres, Redis and one in-memory Identity, shared by all tests (see Step 06).</summary>
public sealed class IdentityFixture : IAsyncLifetime
{
    private const long Megabyte = 1024 * 1024;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgis/postgis:17-3.5")
        .WithCreateParameterModifier(p => p.HostConfig!.Memory = p.HostConfig.MemorySwap = 512 * Megabyte)
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:8.6")
        .WithCreateParameterModifier(p => p.HostConfig!.Memory = p.HostConfig.MemorySwap = 128 * Megabyte)
        .Build();

    private readonly FakeOtpSender _sender = new();
    private WebApplicationFactory<Program>? _factory;
    private ConnectionMultiplexer? _redisAdmin;

    private WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("The fixture is not initialised yet.");

    /// <summary>A connection of its own, with admin commands (FLUSHDB, CLIENT PAUSE) that the app doesn't have.</summary>
    public IDatabase Redis =>
        (_redisAdmin ?? throw new InvalidOperationException("The fixture is not initialised yet.")).GetDatabase();

    public string ConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        _redisAdmin = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString() + ",allowAdmin=true");
        // Starting the app applies the migrations, so they are tested on every run.
        _factory = CreateFactory();
    }

    /// <summary>Another Identity on the same database, with some settings changed.</summary>
    public WebApplicationFactory<Program> CreateFactory(params (string Key, string? Value)[] settings) => CreateFactory(fakeSender: true, settings);

    public WebApplicationFactory<Program> CreateFactory(bool fakeSender, params (string Key, string? Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            (string Key, string? Value)[] defaults =
            [
                ("environment", "Development"),
                ("ConnectionStrings:identitydb", ConnectionString),
                ("ConnectionStrings:redis", _redis.GetConnectionString()),
                ("Otp:HmacKey", "test-key-test-key-test-key-test-key-test-key"),
                ("Otp:From", "no-reply@example.com"),
                // No limits, unless a test is about them.
                ("IpRateLimit:Global", "1000000"),
                ("IpRateLimit:OtpRequest", "1000000"),
                ("IpRateLimit:OtpVerify", "1000000"),
                ("OtpRateLimit:Gap", "00:00:00"),
                ("OtpRateLimit:PerHour", "1000"),
                ("OtpRateLimit:PerDay", "1000"),
            ];
            foreach (var (key, value) in defaults.Concat(settings)) // later settings win
            {
                host.UseSetting(key, value);
            }

            host.ConfigureTestServices(services =>
            {
                services.AddTransient<IStartupFilter, ClientAddressFilter>();
                if (fakeSender)
                {
                    services.Replace(ServiceDescriptor.Singleton<IOtpSender>(_sender));
                }
            });
        });

    public HttpClient CreateClient() => Factory.CreateClient();

    public AsyncServiceScope CreateScope() => Factory.Services.CreateAsyncScope();

    /// <summary>The code that was last "emailed" to this address.</summary>
    public string CodeFor(string email) => _sender.Codes[email];

    /// <summary>Empties users, user_roles and Redis. The roles table is reference data and stays.</summary>
    public async Task ResetAsync()
    {
        await using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await db.Database.ExecuteSqlAsync($"TRUNCATE users CASCADE");
        await Redis.ExecuteAsync("FLUSHDB");
        _sender.Reset();
    }

    internal void MakeSendingFail() => _sender.Fails = true;

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_redisAdmin is not null)
        {
            await _redisAdmin.DisposeAsync();
        }

        await _redis.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>The in-memory server has no real connections, so a test header sets the client address.</summary>
    private sealed class ClientAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextStep) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["Test-Client"].FirstOrDefault() ?? "192.0.2.1");
                return nextStep(context);
            });
            next(app);
        };
    }

    private sealed class FakeOtpSender : IOtpSender
    {
        public ConcurrentDictionary<string, string> Codes { get; } = new();

        public bool Fails { get; set; }

        public Task SendAsync(string email, string code, CancellationToken ct)
        {
            if (Fails)
            {
                throw new IOException("The mail server is down.");
            }

            Codes[email] = code;
            return Task.CompletedTask;
        }

        public void Reset()
        {
            Codes.Clear();
            Fails = false;
        }
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

    protected Task<HttpResponseMessage> RequestCodeAsync(string? email, HttpClient? client = null) =>
        (client ?? Client).PostAsJsonAsync("/auth/otp/request", new RequestOtp.Request(email), Ct);

    protected Task<HttpResponseMessage> VerifyAsync(string? email, string? code, HttpClient? client = null) =>
        (client ?? Client).PostAsJsonAsync("/auth/otp/verify", new VerifyOtp.Request(email, code), Ct);

    /// <summary>Requests a code and returns it.</summary>
    protected async Task<string> GetCodeAsync(string email, HttpClient? client = null)
    {
        using var response = await RequestCodeAsync(email, client);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return Fixture.CodeFor(email);
    }

    internal async Task<VerifyOtp.Response> SignInAsync(string email)
    {
        using var response = await VerifyAsync(email, await GetCodeAsync(email));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<VerifyOtp.Response>(Ct);
        Assert.NotNull(user);
        return user;
    }

    public async ValueTask InitializeAsync() => await Fixture.ResetAsync();

    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
