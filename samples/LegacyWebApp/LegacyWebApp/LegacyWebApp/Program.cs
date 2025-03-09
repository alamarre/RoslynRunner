using System;
using System.Web.Http;
using System.Web.Http.SelfHost;

namespace LegacyWebApp;
class Program
{
    static void Main(string[] args)
    {
        var config = new HttpSelfHostConfiguration("http://localhost:8080");

        // Configure routes
        config.Routes.MapHttpRoute(
            name: "API Default",
            routeTemplate: "api/{controller}/{id}",
            defaults: new { id = RouteParameter.Optional }
        );

        using var server = new HttpSelfHostServer(config);
        server.OpenAsync().Wait();
        Console.WriteLine("Web API Hosted on http://localhost:8080");
        Console.WriteLine("Press Enter to quit.");
        Console.ReadLine();
    }
}
