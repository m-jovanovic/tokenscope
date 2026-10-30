using System.Threading.Channels;
using TokenScope.Api.Shared.Usage;

namespace TokenScope.Api.Features.Telemetry;

// Decouples the HTTP receiver from the database. The receiver never waits on Postgres.
public sealed class IngestQueue
{
    private const int Capacity = 50_000;
    private readonly object _gate = new();
    private long _accepted;
    private long _rejected;

    private readonly Channel<ApiRequestRow> _channel = Channel.CreateBounded<ApiRequestRow>(
        new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    public int Count => _channel.Reader.Count;
    public long Accepted => Interlocked.Read(ref _accepted);
    public long Rejected => Interlocked.Read(ref _rejected);
    public ChannelReader<ApiRequestRow> Reader => _channel.Reader;

    public bool TryEnqueueAll(IReadOnlyCollection<ApiRequestRow> rows)
    {
        // Serialize producers. The consumer can only free space while this lock is held.
        lock (_gate)
        {
            if (_channel.Reader.Count + rows.Count > Capacity)
            {
                Interlocked.Add(ref _rejected, rows.Count);
                return false;
            }

            foreach (var row in rows)
            {
                if (!_channel.Writer.TryWrite(row))
                {
                    throw new InvalidOperationException("Unexpected closed ingest queue.");
                }
            }

            Interlocked.Add(ref _accepted, rows.Count);
            return true;
        }
    }
}
