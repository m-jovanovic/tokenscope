namespace TokenScope.Api.Features.LocalUsage.GetStatus;

public static class GetLocalUsageStatusEndpoint
{
    public static IResult Handle(LocalUsageCollector collector)
    {
        return Results.Ok(collector.Snapshot());
    }
}
