using System.Text.Json;
using TokenScope.Api.Infrastructure.Http;

namespace TokenScope.Api.Features.Telemetry;

public static class OtlpLogsEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/logs", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        IngestQueue queue,
        IConfiguration configuration,
        ILogger<IngestQueue> logger,
        CancellationToken cancellationToken)
    {
        if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
        {
            return Results.Text(
                "TokenScope accepts OTLP/HTTP JSON only. Set OTEL_EXPORTER_OTLP_LOGS_PROTOCOL=http/json.",
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        var json = await BoundedUpload.ReadAsync(request, cancellationToken);
        using var payload = JsonDocument.Parse(json);
        if (payload.RootElement.ValueKind != JsonValueKind.Object ||
            !payload.RootElement.TryGetProperty("resourceLogs", out var resourceLogs) ||
            resourceLogs.ValueKind != JsonValueKind.Array)
        {
            return Results.BadRequest(new
            {
                error = "Expected an OTLP resourceLogs array."
            });
        }

        var defaultDeveloper = configuration["Ingestion:DefaultDeveloper"] ?? "me";
        var rows = OtlpMapper.MapApiRequests(payload.RootElement, defaultDeveloper);

        // Reject the entire batch when full so the exporter can retry it later.
        if (!queue.TryEnqueueAll(rows))
        {
            logger.LogWarning("Ingest queue full, rejecting {Count} events", rows.Count);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Json(new
        {
        });
    }
}
