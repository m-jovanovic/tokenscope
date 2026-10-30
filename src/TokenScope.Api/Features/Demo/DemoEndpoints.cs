namespace TokenScope.Api.Features.Demo;

public static class DemoEndpoints
{
    public static void MapDemoApi(this IEndpointRouteBuilder app)
    {
        var demo = app.MapGroup("/api/demo");
        SeedDemo.MapEndpoint(demo);
        SendDemoPulse.MapEndpoint(demo);
    }
}
