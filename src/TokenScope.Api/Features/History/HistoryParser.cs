using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TokenScope.Api.Shared.Usage;

namespace TokenScope.Api.Features.History;

public static partial class HistoryParser
{
    public static HistoryParseResult Parse(string jsonl)
    {
        var rows = new Dictionary<string, ApiRequestRow>();
        var aliases = new Dictionary<string, string>();
        var issues = new List<string>();
        var records = 0;
        var duplicates = 0;
        var skipped = 0;
        var synthetic = false;

        using var reader = new StringReader(jsonl.TrimStart('\uFEFF'));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            records++;
            if (records > 50_000)
            {
                throw new InvalidDataException("At most 50,000 JSONL records are accepted.");
            }

            try
            {
                if (line.Length > 1_048_576)
                {
                    throw new InvalidDataException("Record exceeds the per-line limit.");
                }

                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Expected a JSON object.");
                }

                if (Text(root, "type") == "tokenscope_fixture")
                {
                    synthetic |= root.TryGetProperty("synthetic", out var flag) && flag.ValueKind == JsonValueKind.True;
                    skipped++;
                    continue;
                }

                if (Text(root, "type") != "assistant")
                {
                    skipped++;
                    continue;
                }

                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
                    !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Assistant record has no usage object.");
                }

                if (!usage.TryGetProperty("input_tokens", out _) || !usage.TryGetProperty("output_tokens", out _))
                {
                    throw new InvalidDataException("Input and output token usage is required.");
                }

                var sessionId = Text(root, "sessionId");
                var requestId = Text(root, "requestId");
                var messageId = Text(message, "id");
                if (sessionId is null || requestId is null && messageId is null)
                {
                    throw new InvalidDataException("Session and request/message identifiers are required.");
                }

                var timestamp = Text(root, "timestamp");
                if (timestamp is null ||
                    !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ||
                    !Regex.IsMatch(timestamp, @"(Z|[+-]\d\d:\d\d)$") ||
                    time < DateTimeOffset.UnixEpoch || time > DateTimeOffset.UtcNow.AddMinutes(5))
                {
                    throw new InvalidDataException("A valid timestamp with a UTC offset is required.");
                }

                var model = Text(message, "model");
                if (model is null || !ModelId().IsMatch(model))
                {
                    throw new InvalidDataException("A recognizable model identifier is required.");
                }

                var inputTokens = UsageTokens.Read(usage, "input_tokens");
                var outputTokens = UsageTokens.Read(usage, "output_tokens");
                var cacheReadTokens = UsageTokens.Read(usage, "cache_read_input_tokens");
                var cacheCreationTokens = UsageTokens.Read(usage, "cache_creation_input_tokens");
                var price = ClaudePricing.Price(
                    root, message, usage, model, inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens);

                var requestAlias = requestId is null ? null : UsageIdentity.Hash(sessionId + "|request|" + requestId);
                var messageAlias = messageId is null ? null : UsageIdentity.Hash(sessionId + "|message|" + messageId);
                var existingKey = new[] { requestAlias, messageAlias }
                    .Where(alias => alias is not null)
                    .Select(alias => aliases.GetValueOrDefault(alias!))
                    .FirstOrDefault(canonicalKey => canonicalKey is not null);
                var key = requestAlias ?? existingKey ?? messageAlias!;

                // Prefer request IDs even when an earlier partial contained only a message ID.
                if (existingKey is not null && existingKey != key && rows.Remove(existingKey, out var existingRow))
                {
                    rows[key] = existingRow with
                    {
                        EventKey = key
                    };
                    var matchingAliases = aliases
                        .Where(pair => pair.Value == existingKey)
                        .Select(pair => pair.Key)
                        .ToArray();
                    foreach (var alias in matchingAliases)
                    {
                        aliases[alias] = key;
                    }
                }

                var row = new ApiRequestRow(
                    time.UtcDateTime, "history", "local-history",
                    "history-" + UsageIdentity.Hash(sessionId)[..16], model, null, null, null, null,
                    inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens, price.Cost, null, key, price.Source);

                if (rows.TryGetValue(key, out var prior))
                {
                    if (prior.Model != model)
                    {
                        throw new InvalidDataException("Conflicting models for one request.");
                    }

                    duplicates++;
                    // Streamed entries repeat cumulative usage. Keep maxima, never add snapshots.
                    inputTokens = Math.Max(inputTokens, prior.InputTokens);
                    outputTokens = Math.Max(outputTokens, prior.OutputTokens);
                    cacheReadTokens = Math.Max(cacheReadTokens, prior.CacheReadTokens);
                    cacheCreationTokens = Math.Max(cacheCreationTokens, prior.CacheCreationTokens);
                    price = ClaudePricing.Price(
                        root, message, usage, model, inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens);
                    if (price.Source == "reported-in-jsonl" && prior.CostSource == price.Source)
                    {
                        price = (Math.Max(price.Cost ?? 0, prior.CostUsdMicros ?? 0), price.Source);
                    }

                    row = row with
                    {
                        Time = prior.Time < row.Time ? prior.Time : row.Time,
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens,
                        CacheReadTokens = cacheReadTokens,
                        CacheCreationTokens = cacheCreationTokens,
                        CostUsdMicros = price.Cost,
                        CostSource = price.Source,
                    };
                }

                rows[key] = row;
                if (requestAlias is not null)
                {
                    aliases[requestAlias] = key;
                }

                if (messageAlias is not null)
                {
                    aliases[messageAlias] = key;
                }
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidDataException or InvalidOperationException or OverflowException)
            {
                skipped++;
                // Never echo the line, prompt, path, account, session identifier or exception data.
                if (issues.Count < 20)
                {
                    issues.Add($"Line {records}: unsupported or invalid usage record; skipped.");
                }
            }
        }

        if (rows.Values.Any(row => row.CostUsdMicros is null))
        {
            issues.Add("Some requests are unpriced: unknown models, cache duration, or unsupported pricing mode. Their tokens still count.");
        }

        issues.Add("Only standard API-equivalent token estimates are covered; subscription bills, tool fees and historical price changes are not reconstructed.");
        return new HistoryParseResult(
            records, duplicates, skipped, synthetic,
            rows.Values.Select(row => row with { IsSynthetic = synthetic }).ToArray(), issues);
    }

    private static string? Text(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 and <= 200 } text
            ? text
            : null;

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,99}$")]
    private static partial Regex ModelId();
}
