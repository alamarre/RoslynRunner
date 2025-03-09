var builder = DistributedApplication.CreateBuilder(args);

var webApi = builder.AddProject<Projects.ModernWebApi>("web");

if (OperatingSystem.IsWindows())
{
    var legacyApi = builder.AddProject<Projects.LegacyWebApp>("legacy-web");
    webApi.WithReference(webApi);
}

builder.Build().Run();
