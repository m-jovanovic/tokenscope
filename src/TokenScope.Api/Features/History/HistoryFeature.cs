namespace TokenScope.Api.Features.History;

public static class HistoryFeature
{
    public static IServiceCollection AddHistory(this IServiceCollection services)
    {
        services.AddSingleton<HistoryPreviews>();
        return services;
    }

    public static void MapHistory(this IEndpointRouteBuilder endpoints)
    {
        PreviewHistory.MapEndpoint(endpoints);
        ImportHistory.MapEndpoint(endpoints);
    }
}
