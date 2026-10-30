namespace TokenScope.Api.Features.Telemetry;

public static class TelemetryFeature
{
    public static IServiceCollection AddTelemetry(this IServiceCollection services)
    {
        services.AddSingleton<IngestQueue>();
        services.AddSingleton<IngestState>();
        services.AddHostedService<IngestWorker>();
        return services;
    }

    public static void MapTelemetry(this IEndpointRouteBuilder endpoints)
    {
        OtlpLogsEndpoint.MapEndpoint(endpoints);
    }
}
