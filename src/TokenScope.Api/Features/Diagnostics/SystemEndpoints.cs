namespace TokenScope.Api.Features.Diagnostics;

public static class SystemEndpoints
{
    public static void MapSystemApi(this IEndpointRouteBuilder app)
    {
        GetHealth.MapEndpoint(app);
        GetSystemStatus.MapEndpoint(app);
    }
}
