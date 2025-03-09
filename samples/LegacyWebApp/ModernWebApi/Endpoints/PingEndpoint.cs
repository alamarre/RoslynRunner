namespace ModernWebApi.Endpoints;

public partial class PingEndpoint
{
    [Api(HttpVerb.Get, "ping-endpoint")]
    public IResult Ping()
    {
        return TypedResults.Text("pong");
    }
}
