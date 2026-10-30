namespace TokenScope.Api.Features.Analytics;

public static class AnalyticsEndpoints
{
    public static void MapAnalyticsApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        GetSummary.MapEndpoint(api);
        GetTimeSeries.MapEndpoint(api);
        GetBreakdown.MapEndpoint(api);
        GetRecentActivity.MapEndpoint(api);
        GetSessions.MapEndpoint(api);
        CompareQueries.MapEndpoint(api);
    }
}
