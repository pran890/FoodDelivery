// SANDBOX: a throwaway API for learning steps 02-05. Deleted once real services exist.
using Npgsql;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Step 03: connection strings are ordinary configuration. The AppHost's WithReference
// injects them as environment variables ConnectionStrings__sandboxdb / ConnectionStrings__redis,
// and GetConnectionString("x") reads the section ConnectionStrings:x.
var postgresConnectionString = builder.Configuration.GetConnectionString("sandboxdb")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:sandboxdb. Start Kitchen through the AppHost.");
var redisConnectionString = builder.Configuration.GetConnectionString("redis")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:redis. Start Kitchen through the AppHost.");

// Both are long-lived, thread-safe objects that pool connections: register them once (singleton).
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgresConnectionString));
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var options = ConfigurationOptions.Parse(redisConnectionString);
    // StackExchange.Redis refuses "admin" commands (INFO, CONFIG, FLUSHALL, ...) unless the
    // connection opts in. SANDBOX ONLY: real services never enable this.
    options.AllowAdmin = true;
    return ConnectionMultiplexer.Connect(options);
});

var app = builder.Build();

// Reports which process and address served the request, so you can see
// where service discovery actually sent the waiter's call.
app.MapGet("/dish-of-the-day", (HttpContext http) => new DishOfTheDay(
    Dish: "Dal Baati Churma",
    CookedBy: $"kitchen (pid {Environment.ProcessId})",
    ListeningOn: $"{http.Request.Scheme}://{http.Request.Host}"));

// Proves the Postgres connection string works. Reads only server metadata: no tables are created.
app.MapGet("/infra/postgres", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var cmd = db.CreateCommand("""
        SELECT current_database(),
               version(),
               (SELECT default_version FROM pg_available_extensions WHERE name = 'postgis'),
               current_setting('shared_buffers'),
               current_setting('max_connections')
        """);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    await reader.ReadAsync(ct);
    return new PostgresInfo(
        Database: reader.GetString(0),
        Version: reader.GetString(1),
        PostgisAvailable: reader.IsDBNull(2) ? null : reader.GetString(2),
        SharedBuffers: reader.GetString(3),
        MaxConnections: reader.GetString(4));
});

// Proves the Redis connection string works, and shows data surviving restarts (volume).
app.MapGet("/infra/redis", async (IConnectionMultiplexer redis) =>
{
    var served = await redis.GetDatabase().StringIncrementAsync("sandbox:kitchen:orders-served");
    var server = redis.GetServer(redis.GetEndPoints()[0]);
    var memory = (await server.InfoAsync("memory"))
        .SelectMany(group => group)
        .FirstOrDefault(kv => kv.Key == "used_memory_human").Value;
    return new RedisInfo(OrdersServed: served, UsedMemory: memory);
});

// Learning aid: the connection strings the AppHost injected, with secrets masked.
app.MapGet("/debug/connection-strings", (IConfiguration config) =>
    config.GetSection("ConnectionStrings").GetChildren().ToDictionary(
        c => c.Key,
        c => System.Text.RegularExpressions.Regex.Replace(c.Value ?? "", "(?i)(password=)[^;,]*", "$1*****")));

app.Run();

internal sealed record DishOfTheDay(string Dish, string CookedBy, string ListeningOn);

internal sealed record PostgresInfo(string Database, string Version, string? PostgisAvailable, string SharedBuffers, string MaxConnections);

internal sealed record RedisInfo(long OrdersServed, string? UsedMemory);
