using Microsoft.AspNetCore.Mvc;

namespace LegacyWebApp.Controllers;

[ApiController]
public class SampleController : ControllerBase
{
    [HttpGet("legacy/ping")]
    public string LegacyPing()
    {
        return "pong";
    }

    [HttpGet("ping")]
    public string Ping()
    {
        return "pong";
    }
}
