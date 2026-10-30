using System.Collections.Concurrent;

namespace TokenScope.Api.Features.History;

public sealed class HistoryPreviews
{
    public sealed record Preview(string Id, DateTime ExpiresAt, HistoryParseResult Data);

    private readonly ConcurrentDictionary<string, Preview> _previews = new();
    private readonly object _gate = new();

    public Preview Add(HistoryParseResult data)
    {
        lock (_gate)
        {
            foreach (var (id, preview) in _previews)
            {
                if (preview.ExpiresAt <= DateTime.UtcNow)
                {
                    _previews.TryRemove(id, out _);
                }
            }

            if (_previews.Count >= 8)
            {
                throw new BadHttpRequestException("Too many previews. Wait for a preview to expire.", 429);
            }

            var added = new Preview(Guid.NewGuid().ToString("N"), DateTime.UtcNow.AddMinutes(10), data);
            _previews[added.Id] = added;
            return added;
        }
    }

    public Preview? Get(string id) =>
        _previews.TryGetValue(id, out var preview) && preview.ExpiresAt > DateTime.UtcNow
            ? preview
            : null;
}
