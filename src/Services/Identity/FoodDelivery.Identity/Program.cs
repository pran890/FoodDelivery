using FoodDelivery.Identity.Data;
using FoodDelivery.Identity.Features.Users;
using Microsoft.EntityFrameworkCore;

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

builder.Services.AddHealthChecks()
    .AddCheck<IdentityDbHealthCheck>("identitydb", timeout: TimeSpan.FromSeconds(3));

var app = builder.Build();
app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    await app.MigrateAndSeedAsync();

    CreateUser.Map(app);
    GetUser.Map(app);
}

app.Run();
