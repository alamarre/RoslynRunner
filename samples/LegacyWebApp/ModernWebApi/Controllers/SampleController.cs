using Microsoft.AspNetCore.Mvc;

namespace ModernWebApi.Controllers;

[ApiController]
public class SampleController : ControllerBase
{
    private readonly ModernWebApi.Services.IValuesService _service;

    public SampleController(ModernWebApi.Services.IValuesService service)
    {
        _service = service;
    }
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

    [HttpGet("values/{id}")]
    public IEnumerable<int> Values(int id)
    {
        return _service.GetValues(id);
    }
}
