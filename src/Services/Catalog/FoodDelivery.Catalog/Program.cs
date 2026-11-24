using FoodDelivery.Catalog.Features;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddFoodDeliveryJwt();
builder.Services.AddProblemDetails();

var app = builder.Build();
app.MapDefaultEndpoints();

WhoAmI.Map(app);

app.Run();
