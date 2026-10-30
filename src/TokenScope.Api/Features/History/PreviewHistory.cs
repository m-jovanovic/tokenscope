using TokenScope.Api.Infrastructure.Http;

namespace TokenScope.Api.Features.History;

public static class PreviewHistory
{
    public sealed record Request(string Jsonl);

    public static void MapEndpoint(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/history/preview", EndpointAsync);
    }

    private static async Task<IResult> EndpointAsync(
        HttpRequest request,
        HistoryPreviews previews,
        CancellationToken cancellationToken)
    {
        if (request.ContentType?.Split(';')[0] is not ("application/x-ndjson" or "application/jsonl" or "text/plain"))
        {
            return Results.Json(
                new
                {
                    error = "Upload raw JSONL with Content-Type application/x-ndjson."
                },
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        var jsonl = await BoundedUpload.ReadAsync(request, cancellationToken);
        return Handle(new Request(jsonl), previews);
    }

    public static IResult Handle(Request request, HistoryPreviews previews)
    {
        var result = HistoryParser.Parse(request.Jsonl);
        if (result.Rows.Count == 0)
        {
            return Results.BadRequest(new
            {
                error = "No supported assistant usage records.",
                result.Issues
            });
        }

        var preview = previews.Add(result);
        return Results.Ok(HistoryResponse.From(preview));
    }
}
