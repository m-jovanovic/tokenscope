using TokenScope.Api.Features.LocalUsage.GetStatus;
using TokenScope.Api.Features.LocalUsage.Sync;

namespace TokenScope.Api.Features.LocalUsage;

public static class LocalUsageModule
{
    public static IServiceCollection AddLocalUsage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<LocalUsageOptions>(configuration.GetSection("LocalUsage"));
        services.AddSingleton<LocalUsageStore>();
        services.AddSingleton<LocalUsageCollector>();
        services.AddHostedService(provider => provider.GetRequiredService<LocalUsageCollector>());
        return services;
    }

    public static void MapLocalUsage(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/local-usage/status", GetLocalUsageStatusEndpoint.Handle);
        endpoints.MapPost("/api/local-usage/sync", SyncLocalUsageEndpoint.Handle);
    }
}
