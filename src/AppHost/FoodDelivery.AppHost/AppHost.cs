using StackExchange.Redis;

var builder = DistributedApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Infrastructure containers (Step 03)
// Every container: Persistent lifetime (survives AppHost restarts), a named
// volume (data survives container removal), a hard memory cap (12 GB laptop).
// --memory-swap must equal --memory: with --memory alone Docker ALSO allows the same amount
// again as swap, so a 128m cap really means 256m (found the hard way in Step 03).
// ---------------------------------------------------------------------------

// Postgres with PostGIS in one container. Each service gets its own database
// on it (added in that service's step).
var postgres = builder.AddPostgres("postgres")
    .WithImage("postgis/postgis")
    .WithImageTag("17-3.5")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("fooddelivery-postgres-data")
    .WithContainerRuntimeArgs("--memory=512m", "--memory-swap=512m")
    // Arguments starting with "-" are passed to the postgres server by the image's
    // entrypoint. Smaller than the defaults to fit the RAM budget.
    .WithArgs("-c", "shared_buffers=128MB", "-c", "max_connections=50");

var identityDb = postgres.AddDatabase("identitydb");


var redis = builder.AddRedis("redis")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("fooddelivery-redis-data")
    .WithContainerRuntimeArgs("--memory=128m", "--memory-swap=128m")
    // Step 09: below the container cap, so a full Redis refuses writes with an error we can see,
    // instead of being killed by Docker. AddRedis has no setting for it, so it is applied once Redis
    // is ready. (The eviction policy is already noeviction by default: never drop codes silently.)
    .OnResourceReady(async (resource, _, ct) =>
    {
        var connectionString = await resource.ConnectionStringExpression.GetValueAsync(ct);
        await using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString + ",allowAdmin=true");
        await connection.GetServer(connection.GetEndPoints()[0]).ConfigSetAsync("maxmemory", "96mb");
    });

// Fake SMTP server with a web inbox: it catches every email (Step 09: the OTP codes).
var mailpit = builder.AddMailPit("mailpit")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithContainerRuntimeArgs("--memory=64m", "--memory-swap=64m");

// Generated on the first run and kept in the AppHost user secrets, like the container passwords.
var otpHmacKey = builder.AddParameter(
    "otp-hmac-key", new GenerateParameterDefault { MinLength = 44 }, secret: true, persist: true);

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------

// A "resource" is anything the AppHost manages: a .NET project, a container, an executable.
// The string is its NAME: the dashboard shows it, and other services use it to find this one
// ("https+http://identity").
var identity = builder.AddProject<Projects.FoodDelivery_Identity>("identity")
    // For a database or cache, WithReference injects a connection string (ConnectionStrings__identitydb).
    .WithReference(identityDb)
    .WithReference(redis)
    .WithReference(mailpit)
    .WithEnvironment("Otp__HmacKey", otpHmacKey)
    .WaitFor(identityDb)
    .WaitFor(redis)
    // The dashboard polls readiness, and WaitFor(identity) waits for HEALTHY, not just "started".
    .WithHttpHealthCheck("/health");

// Step 12: Catalog (a skeleton so far) checks tokens with the public keys it gets from Identity.
builder.AddProject<Projects.FoodDelivery_Catalog>("catalog")
    // For a service, WithReference injects its addresses (services__identity__https__0).
    .WithReference(identity)
    .WaitFor(identity)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
