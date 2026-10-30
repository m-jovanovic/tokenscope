namespace TokenScope.Api.Features.Telemetry;

public sealed class IngestState
{
    private readonly object _gate = new();
    private long _persisted;
    private long _duplicates;
    private DateTime? _lastPersistedAt;
    private string? _lastError;

    public void Saved(int inserted, int duplicates)
    {
        lock (_gate)
        {
            _persisted += inserted;
            _duplicates += duplicates;
            if (inserted > 0)
            {
                _lastPersistedAt = DateTime.UtcNow;
            }

            _lastError = null;
        }
    }

    public void Failed()
    {
        lock (_gate)
        {
            _lastError = "Database write failed; the current batch is being retried.";
        }
    }

    public object Snapshot(IngestQueue queue)
    {
        lock (_gate)
        {
            return new
            {
                queued = queue.Count,
                accepted = queue.Accepted,
                persisted = _persisted,
                duplicates = _duplicates,
                rejected = queue.Rejected,
                lastPersistedAt = _lastPersistedAt,
                lastError = _lastError,
            };
        }
    }
}
