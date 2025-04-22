using Microsoft.AspNetCore.Mvc;

namespace ModernWebApi.Controllers;

[ApiController]
public class SampleController : ControllerBase
{
    [HttpGet("ping")]
    public string Ping()
    {
        return "pong";
    }

    [HttpGet]
    public string MethodWithDependencies()
    {
        return GetDependencyResponse();
    }

    private string GetDependencyResponse()
    {
        return "DependencyResponse";
    }
}
