namespace TokenScope.Api.Features.LocalUsage.Sync;

public static class SyncLocalUsageEndpoint
{
    public static IResult Handle(LocalUsageCollector collector)
    {
        return Results.Ok(new
        {
            accepted = collector.RequestSync()
        });
    }
}
