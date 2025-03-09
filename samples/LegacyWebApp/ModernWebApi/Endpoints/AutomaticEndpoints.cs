namespace ModernWebApi.Endpoints;

public partial class AutomaticEndpoints
{
    partial void _RegisterEndpointMappers(IServiceCollection services);

    public void RegisterEndpointMappers(IServiceCollection services)
    {
        _RegisterEndpointMappers(services);
    }

    public void RegisterEndpoints(WebApplication application)
    {
        var mappers = application.Services.GetServices<IEndpointMapper>();
        foreach (var mapper in mappers)
        {
            mapper.MapEndpoints(application);
        }
    }
}
