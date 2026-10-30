using Npgsql;
using TokenScope.Api.Infrastructure.Database;

namespace TokenScope.Api.Features.History;

public static class ImportHistory
{
    public sealed record Request(string PreviewId);

    public static void MapEndpoint(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/history/import", HandleAsync);
    }

    public static async Task<IResult> HandleAsync(
        Request request,
        HistoryPreviews previews,
        UsageEventWriter writer,
        NpgsqlDataSource database,
        CancellationToken cancellationToken)
    {
        var preview = previews.Get(request.PreviewId ?? "");
        if (preview is null)
        {
            return Results.BadRequest(new
            {
                error = "Preview expired or does not exist. Preview the file again.",
            });
        }

        var inserted = await writer.InsertAsync(preview.Data.Rows.ToArray(), cancellationToken);
        // Retry refresh even when rows were already imported: a previous refresh may have failed after commit.
        var earliestRequest = preview.Data.Rows.Min(row => row.Time);
        await ContinuousAggregateRefresher.RefreshAsync(database, earliestRequest, cancellationToken);

        return Results.Ok(HistoryResponse.From(preview, inserted));
    }
}
