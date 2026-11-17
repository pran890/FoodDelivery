var builder = DistributedApplication.CreateBuilder(args);

// A "resource" is anything the AppHost manages: a .NET project, a container, an executable.
// The string is the resource NAME. The dashboard shows it, and other services use it to
// find this one ("https+http://kitchen").
var kitchen = builder.AddProject<Projects.FoodDelivery_Sandbox_Kitchen>("kitchen");

builder.AddProject<Projects.FoodDelivery_Sandbox_Waiter>("waiter")
    // WithReference: inject kitchen's endpoints into waiter's configuration as
    // environment variables (services__kitchen__https__0, services__kitchen__http__0).
    .WithReference(kitchen)
    // WaitFor: don't start waiter until kitchen is running.
    .WaitFor(kitchen);

builder.Build().Run();
