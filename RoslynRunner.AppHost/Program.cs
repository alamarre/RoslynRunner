var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.RoslynRunner>("runner");

builder.Build().Run();
