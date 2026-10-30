using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TokenScope.Api.Shared.Usage;

namespace TokenScope.Api.Features.LocalUsage;

public static partial class ClaudeUsageParser
{
    public static ApiRequestRow? ParseLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(
                line.TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (!TryReadAssistantUsage(root, out var message, out var usage))
            {
                return null;
            }

            var session = ReadText(root, "sessionId");
            var identity = ReadText(root, "requestId") ?? ReadText(message, "id");
            var model = ReadText(message, "model");
            if (session is null || identity is null || model is null || !ModelId().IsMatch(model) ||
                !TryReadTimestamp(root, out var timestamp))
            {
                return null;
            }

            if (!usage.TryGetProperty("input_tokens", out _) ||
                !usage.TryGetProperty("output_tokens", out _))
            {
                return null;
            }

            var inputTokens = UsageTokens.Read(usage, "input_tokens");
            var outputTokens = UsageTokens.Read(usage, "output_tokens");
            var cacheReadTokens = UsageTokens.Read(usage, "cache_read_input_tokens");
            var cacheCreationTokens = UsageTokens.Read(usage, "cache_creation_input_tokens");
            var price = ClaudePricing.Price(
                root, message, usage, model, inputTokens, outputTokens, cacheReadTokens,
                cacheCreationTokens, assumeStandardApiRates: true);

            return new ApiRequestRow(
                Time: timestamp.UtcDateTime,
                Source: UsageSources.ClaudeLocal,
                Developer: "local-user",
                SessionId: "claude-" + UsageIdentity.Hash(session)[..16],
                Model: model,
                QuerySource: "claude-local-request",
                AgentName: null,
                SkillName: null,
                McpServer: null,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                CacheReadTokens: cacheReadTokens,
                CacheCreationTokens: cacheCreationTokens,
                CostUsdMicros: price.Cost,
                DurationMs: null,
                EventKey: UsageIdentity.Hash("claude-request|" + session + "|" + identity),
                CostSource: price.Source);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                        InvalidDataException or OverflowException or FormatException)
        {
            return null;
        }
    }

    private static bool TryReadAssistantUsage(
        JsonElement root,
        out JsonElement message,
        out JsonElement usage)
    {
        message = default;
        usage = default;
        return root.ValueKind == JsonValueKind.Object &&
               ReadText(root, "type") == "assistant" &&
               root.TryGetProperty("message", out message) && message.ValueKind == JsonValueKind.Object &&
               message.TryGetProperty("usage", out usage) && usage.ValueKind == JsonValueKind.Object;
    }

    private static bool TryReadTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        return DateTimeOffset.TryParse(
                   ReadText(root, "timestamp"), CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal, out timestamp) &&
               timestamp >= DateTimeOffset.UnixEpoch &&
               timestamp <= DateTimeOffset.UtcNow.AddMinutes(5);
    }

    private static string? ReadText(JsonElement owner, string key)
    {
        return owner.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
               value.GetString() is { Length: > 0 and <= 200 } text
            ? text
            : null;
    }

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,99}$")]
    private static partial Regex ModelId();
}
