// SANDBOX: a throwaway API for learning steps 02-05. Deleted once real services exist.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var app = builder.Build();

// Reports which process and address served the request, so you can see
// where service discovery actually sent the waiter's call.
app.MapGet("/dish-of-the-day", (HttpContext http) => new DishOfTheDay(
    Dish: "Dal Baati Churma",
    CookedBy: $"kitchen (pid {Environment.ProcessId})",
    ListeningOn: $"{http.Request.Scheme}://{http.Request.Host}"));

app.Run();

internal sealed record DishOfTheDay(string Dish, string CookedBy, string ListeningOn);
