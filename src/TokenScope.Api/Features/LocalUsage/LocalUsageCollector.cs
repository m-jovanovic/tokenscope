using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TokenScope.Api.Shared.Usage;

namespace TokenScope.Api.Features.LocalUsage;

public sealed class LocalUsageCollector(
    IOptions<LocalUsageOptions> settings,
    LocalUsageStore store,
    ILogger<LocalUsageCollector> logger) : BackgroundService
{
    private const int PollSeconds = 5;
    private const int CheckpointLineCount = 500;
    private const int PrefixBytes = 4_096;
    private const int AnchorBytes = 128;
    private const int ReadBufferBytes = 65_536;

    private readonly LocalUsageOptions _options = settings.Value;
    private readonly object _gate = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private Dictionary<string, LocalCursor>? _cursors;

    private bool _scanning;
    private bool _initialScanComplete;
    private bool _pendingRefresh;
    private DateTime? _lastScanAt;
    private DateTime? _lastPersistedAt;
    private int _filesDiscovered;
    private int _filesProcessed;
    private int _failedFiles;
    private long _inserted;
    private long _updated;
    private long _skipped;
    private string? _lastError;
    private ClientStatus[] _clients = [new(UsageSources.ClaudeLocal, false, 0)];

    public sealed record ClientStatus(string Source, bool Available, int Files);

    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                enabled = _options.Enabled,
                scanning = _scanning,
                initialScanComplete = _initialScanComplete,
                pollSeconds = PollSeconds,
                lastScanAt = _lastScanAt,
                lastPersistedAt = _lastPersistedAt,
                filesDiscovered = _filesDiscovered,
                filesProcessed = _filesProcessed,
                failedFiles = _failedFiles,
                inserted = _inserted,
                updated = _updated,
                skippedRecords = _skipped,
                lastError = _lastError,
                pendingRefresh = _pendingRefresh,
                clients = _clients,
            };
        }
    }

    public bool RequestSync()
    {
        if (!_options.Enabled)
        {
            return false;
        }

        _wake.Writer.TryWrite(true);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Local usage scan failed ({ErrorType}); retrying.", exception.GetType().Name);
                lock (_gate)
                {
                    _lastError = "Local collection could not complete; the next poll will retry.";
                    _scanning = false;
                }
            }

            try
            {
                await WaitForNextScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task WaitForNextScanAsync(CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await Task.WhenAny(
            Task.Delay(TimeSpan.FromSeconds(PollSeconds), wait.Token),
            _wake.Reader.WaitToReadAsync(wait.Token).AsTask());
        wait.Cancel();

        while (_wake.Reader.TryRead(out _))
        {
            // Multiple manual sync requests collapse into a single additional scan.
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        BeginScan();
        _cursors ??= await store.LoadCursorsAsync(cancellationToken);
        var pendingRefresh = await store.HasPendingRefreshAsync(cancellationToken);
        lock (_gate)
        {
            _pendingRefresh = pendingRefresh;
            _lastPersistedAt = _cursors.Values.Max(cursor => cursor.LastPersistedAt);
        }

        var available = !string.IsNullOrWhiteSpace(_options.ClaudePath) &&
                        Directory.Exists(_options.ClaudePath);
        var files = DiscoverFiles(available);
        UpdateDiscoveredFiles(available, files.Count);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ScanFileAsync(file, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                            JsonException or InvalidDataException)
            {
                lock (_gate)
                {
                    _failedFiles++;
                    _lastError = "Some local files could not be read; the next poll will retry.";
                }
            }

            lock (_gate)
            {
                _filesProcessed++;
            }
        }

        lock (_gate)
        {
            _pendingRefresh = true;
        }

        await store.RefreshPendingAsync(cancellationToken);
        CompleteScan();
    }

    private void BeginScan()
    {
        lock (_gate)
        {
            _scanning = true;
            _filesProcessed = 0;
            _failedFiles = 0;
            _lastError = null;
        }
    }

    private List<string> DiscoverFiles(bool available)
    {
        var files = new List<string>();
        if (!available)
        {
            return files;
        }

        try
        {
            files.AddRange(Directory.EnumerateFiles(_options.ClaudePath, "*.jsonl", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }));
        }
        catch (IOException)
        {
            lock (_gate)
            {
                _failedFiles++;
            }
        }

        return files.Distinct().ToList();
    }

    private void UpdateDiscoveredFiles(bool available, int count)
    {
        lock (_gate)
        {
            _filesDiscovered = count;
            _clients = [new(UsageSources.ClaudeLocal, available, count)];
            _inserted = _cursors!.Values.Sum(cursor => cursor.Inserted);
            _updated = _cursors.Values.Sum(cursor => cursor.Updated);
            _skipped = _cursors.Values.Sum(cursor => cursor.SkippedRecords);
        }
    }

    private void CompleteScan()
    {
        lock (_gate)
        {
            _pendingRefresh = false;
            _scanning = false;
            _lastScanAt = DateTime.UtcNow;
            if (_failedFiles == 0)
            {
                _initialScanComplete = true;
            }
        }
    }

    private async Task ScanFileAsync(string path, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        var pathHash = UsageIdentity.Hash(Path.GetFullPath(path).Replace('\\', '/'));
        var cursorKey = UsageSources.ClaudeLocal + ":" + pathHash;
        _cursors!.TryGetValue(cursorKey, out var previous);

        if (previous is not null && previous.Length == file.Length &&
            previous.LastWriteUtc == file.LastWriteTimeUtc && previous.Offset >= file.Length)
        {
            return;
        }

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            ReadBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var reset = await MustRestartFileAsync(stream, file, previous, cancellationToken);
        var cursor = previous ?? new LocalCursor { Source = UsageSources.ClaudeLocal, PathHash = pathHash };
        if (reset)
        {
            cursor = cursor with
            {
                Offset = 0,
                PrefixLength = (int)Math.Min(stream.Length, PrefixBytes)
            };
        }

        var prefixHash = await FingerprintAsync(stream, 0, cursor.PrefixLength, cancellationToken);
        var rows = new List<ApiRequestRow>();
        var skipped = 0;
        var lines = 0;

        await foreach (var line in LocalLineReader.ReadAsync(stream, cursor.Offset, cancellationToken))
        {
            var row = line.Text is null ? null : ClaudeUsageParser.ParseLine(line.Text);
            if (row is null)
            {
                skipped++;
            }
            else
            {
                rows.Add(row);
            }

            cursor = cursor with
            {
                Offset = line.NextOffset
            };
            lines++;
            if (lines >= CheckpointLineCount)
            {
                // Checkpoint fingerprints use another handle so they cannot disturb the reader's buffer.
                cursor = await CheckpointAsync(
                    path, cursorKey, cursor, prefixHash, rows, skipped, cancellationToken);
                rows.Clear();
                skipped = 0;
                lines = 0;
            }
        }

        if (lines > 0 || previous is null || reset)
        {
            await CheckpointAsync(path, cursorKey, cursor, prefixHash, rows, skipped, cancellationToken);
        }
    }

    private static async Task<bool> MustRestartFileAsync(
        Stream stream,
        FileInfo file,
        LocalCursor? previous,
        CancellationToken cancellationToken)
    {
        if (previous is null || stream.Length < previous.Offset ||
            (file.Length <= previous.Length && file.LastWriteTimeUtc != previous.LastWriteUtc))
        {
            return true;
        }

        var prefixHash = await FingerprintAsync(stream, 0, previous.PrefixLength, cancellationToken);
        if (prefixHash != previous.PrefixHash)
        {
            return true;
        }

        var anchorHash = await FingerprintAsync(
            stream, Math.Max(0, previous.Offset - AnchorBytes),
            (int)Math.Min(previous.Offset, AnchorBytes), cancellationToken);
        return anchorHash != previous.AnchorHash;
    }

    private async Task<LocalCursor> CheckpointAsync(
        string path,
        string cursorKey,
        LocalCursor cursor,
        string prefixHash,
        List<ApiRequestRow> rows,
        int skipped,
        CancellationToken cancellationToken)
    {
        await using var probe = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var file = new FileInfo(path);
        var anchorHash = await FingerprintAsync(
            probe, Math.Max(0, cursor.Offset - AnchorBytes),
            (int)Math.Min(cursor.Offset, AnchorBytes), cancellationToken);
        cursor = cursor with
        {
            Length = file.Length,
            LastWriteUtc = file.LastWriteTimeUtc,
            PrefixHash = prefixHash,
            AnchorHash = anchorHash,
        };

        var saved = await store.SaveAsync(cursor, rows, skipped, cancellationToken);
        _cursors![cursorKey] = saved.Cursor;
        lock (_gate)
        {
            _inserted += saved.Inserted;
            _updated += saved.Updated;
            _skipped += saved.Skipped;
            if (saved.Inserted + saved.Updated > 0)
            {
                _lastPersistedAt = DateTime.UtcNow;
                _pendingRefresh = true;
            }
        }

        return saved.Cursor;
    }

    private static async Task<string> FingerprintAsync(
        Stream stream,
        long offset,
        int count,
        CancellationToken cancellationToken)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[count];
        var bytesRead = 0;
        while (bytesRead < count)
        {
            var next = await stream.ReadAsync(bytes.AsMemory(bytesRead, count - bytesRead), cancellationToken);
            if (next == 0)
            {
                break;
            }

            bytesRead += next;
        }

        return Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, bytesRead)));
    }
}
