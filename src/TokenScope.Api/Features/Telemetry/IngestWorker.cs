using Npgsql;
using TokenScope.Api.Infrastructure.Database;
using TokenScope.Api.Shared.Usage;

namespace TokenScope.Api.Features.Telemetry;

public sealed class IngestWorker(
    IngestQueue queue,
    UsageEventWriter writer,
    IngestState state,
    ILogger<IngestWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await queue.Reader.WaitToReadAsync(stoppingToken))
        {
            var batch = new List<ApiRequestRow>(5000);
            while (batch.Count < 5000 && queue.Reader.TryRead(out var row))
            {
                batch.Add(row);
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var inserted = await writer.InsertAsync(batch, stoppingToken);
                    state.Saved(inserted, batch.Count - inserted);
                    break;
                }
                catch (NpgsqlException) when (!stoppingToken.IsCancellationRequested)
                {
                    state.Failed();
                    logger.LogWarning("Database write failed for {Count} requests; retry {Attempt}", batch.Count, attempt);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt, 30)), stoppingToken);
                }
            }
        }
    }
}
