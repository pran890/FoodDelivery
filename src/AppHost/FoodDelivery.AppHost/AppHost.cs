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
// on it (added in that service's step); "sandboxdb" is a throwaway for learning.
var postgres = builder.AddPostgres("postgres")
    .WithImage("postgis/postgis")
    .WithImageTag("17-3.5")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("fooddelivery-postgres-data")
    .WithContainerRuntimeArgs("--memory=512m", "--memory-swap=512m")
    // Arguments starting with "-" are passed to the postgres server by the image's
    // entrypoint. Smaller than the defaults to fit the RAM budget.
    .WithArgs("-c", "shared_buffers=128MB", "-c", "max_connections=50");

var sandboxDb = postgres.AddDatabase("sandboxdb");
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

// Step 08: the first real service.
builder.AddProject<Projects.FoodDelivery_Identity>("identity")
    .WithReference(identityDb)
    .WithReference(redis)
    .WithReference(mailpit)
    .WithEnvironment("Otp__HmacKey", otpHmacKey)
    .WaitFor(identityDb)
    .WaitFor(redis)
    .WithHttpHealthCheck("/health");

// SANDBOX (steps 02-05): start kitchen, then waiter by hand in the dashboard (WithExplicitStart).
// A "resource" is anything the AppHost manages: a .NET project, a container, an executable.
// The string is the resource NAME. The dashboard shows it, and other services use it to
// find this one ("https+http://kitchen").
var kitchen = builder.AddProject<Projects.FoodDelivery_Sandbox_Kitchen>("kitchen")
    // For a database or cache, WithReference injects a connection string:
    // ConnectionStrings__sandboxdb and ConnectionStrings__redis.
    .WithReference(sandboxDb)
    .WithReference(redis)
    .WaitFor(sandboxDb)
    .WaitFor(redis)
    // Step 05: the dashboard polls the kitchen's readiness endpoint and shows Healthy/Unhealthy,
    // and anything that WaitFor(kitchen) now waits for HEALTHY, not just "process started".
    .WithHttpHealthCheck("/health")
    .WithExplicitStart();

builder.AddProject<Projects.FoodDelivery_Sandbox_Waiter>("waiter")
    // WithReference: inject kitchen's endpoints into waiter's configuration as
    // environment variables (services__kitchen__https__0, services__kitchen__http__0).
    .WithReference(kitchen)
    // WaitFor: don't start waiter until kitchen is running (Step 05: and healthy).
    .WaitFor(kitchen)
    .WithHttpHealthCheck("/health")
    .WithExplicitStart();

builder.Build().Run();
