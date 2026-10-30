using TokenScope.Api.Infrastructure.Database;
using TokenScope.Api.Shared.Usage;

namespace TokenScope.Api.Features.Demo;

public static class SendDemoPulse
{
    public static void MapEndpoint(IEndpointRouteBuilder demo)
    {
        demo.MapPost("/pulse", HandleAsync);
    }

    public static async Task<IResult> HandleAsync(UsageEventWriter writer, CancellationToken cancellationToken)
    {
        var request = new ApiRequestRow(
            Time: DateTime.UtcNow,
            Source: "demo",
            Developer: "synthetic-pulse",
            SessionId: "demo-pulse",
            Model: "claude-haiku-4-5",
            QuerySource: "synthetic-pulse",
            AgentName: null,
            SkillName: null,
            McpServer: null,
            InputTokens: 1000,
            OutputTokens: 100,
            CacheReadTokens: 0,
            CacheCreationTokens: 0,
            CostUsdMicros: 1500,
            DurationMs: null,
            EventKey: Guid.NewGuid().ToString("N"),
            CostSource: "synthetic",
            IsSynthetic: true);

        var inserted = await writer.InsertAsync([request], cancellationToken);
        return Results.Ok(new Response(inserted, "demo", true, "Synthetic demo pulse", request.Time));
    }

    public sealed record Response(int Inserted, string Source, bool Synthetic, string Label, DateTime At);
}
