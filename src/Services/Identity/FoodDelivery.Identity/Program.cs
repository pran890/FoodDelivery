using FoodDelivery.Identity.Data;
using FoodDelivery.Identity.Features.Auth;
using FoodDelivery.Identity.Features.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();

// The connection string is read when a DbContext is first created, not here, so the dotnet-ef tool
// can build the model without a database.
builder.Services.AddDbContext<IdentityDbContext>((services, options) => options
    .UseNpgsql(
        services.GetRequiredService<IConfiguration>().GetConnectionString("identitydb"),
        // Retries transient failures (a dropped connection, a restarting server), not errors such as
        // a unique violation. The database counterpart of Step 05's HTTP retries.
        npgsql => npgsql.EnableRetryOnFailure())
    .UseSnakeCaseNamingConvention());

// Step 09: one-time codes.
builder.Services.AddSingleton<IConnectionMultiplexer>(services =>
{
    var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString("redis")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:redis.");
    var options = ConfigurationOptions.Parse(connectionString);
    // Start without Redis if need be, and keep trying to connect in the background.
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options);
});
builder.Services.AddOptions<OtpOptions>().BindConfiguration(OtpOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddSingleton<OtpStore>();
builder.Services.AddSingleton<IOtpSender, EmailOtpSender>();
builder.Services.AddExceptionHandler<RedisExceptionHandler>();

builder.Services.AddHealthChecks()
    .AddCheck<IdentityDbHealthCheck>("identitydb", timeout: TimeSpan.FromSeconds(3))
    // Degraded, not Unhealthy: without Redis nobody can sign in, but the rest of Identity (from
    // Step 12 the public keys every other service needs) must keep receiving traffic.
    .AddCheck<RedisHealthCheck>("redis", failureStatus: HealthStatus.Degraded, timeout: TimeSpan.FromSeconds(3));

var app = builder.Build();
app.UseExceptionHandler();
app.MapDefaultEndpoints();

RequestOtp.Map(app);
VerifyOtp.Map(app);

if (app.Environment.IsDevelopment())
{
    await app.MigrateAndSeedAsync();

    GetUser.Map(app);
}

app.Run();
