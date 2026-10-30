using System.Globalization;
using System.Text.Json;
using TokenScope.Api.Shared.Usage;

namespace TokenScope.Api.Features.Telemetry;

public static class OtlpMapper
{
    // Keep only claude_code.api_request events and the explicit usage/attribution allowlist.
    // Event bodies and tool inputs are discarded.
    public static List<ApiRequestRow> MapApiRequests(JsonElement root, string defaultDeveloper)
    {
        var rows = new List<ApiRequestRow>();

        foreach (var resourceLogs in root.ArrayOrEmpty("resourceLogs"))
        {
            var resource = ReadAttributes(resourceLogs.ObjectOrDefault("resource"));

            foreach (var scopeLogs in resourceLogs.ArrayOrEmpty("scopeLogs"))
            {
                foreach (var record in scopeLogs.ArrayOrEmpty("logRecords"))
                {
                    var attributes = ReadAttributes(record);
                    if (!IsApiRequest(record, attributes))
                    {
                        continue;
                    }

                    long? costMicros = attributes.Integer("cost_usd_micros")
                        ?? (attributes.Number("cost_usd") is { } dollars
                            ? checked((long)Math.Round(dollars * 1_000_000))
                            : null);
                    if (costMicros < 0)
                    {
                        throw new InvalidDataException("Negative costs are not accepted.");
                    }

                    rows.Add(new ApiRequestRow(
                        Time: ReadTime(record, attributes),
                        Source: "live",
                        Developer: attributes.Text("developer") ?? resource.Text("developer") ?? defaultDeveloper,
                        SessionId: attributes.Text("session.id") ?? "unknown",
                        Model: attributes.Text("model") ?? "unknown",
                        QuerySource: attributes.Text("query_source"),
                        AgentName: attributes.Text("agent.name"),
                        SkillName: attributes.Text("skill.name"),
                        McpServer: attributes.Text("mcp_server.name"),
                        InputTokens: attributes.Tokens("input_tokens"),
                        OutputTokens: attributes.Tokens("output_tokens"),
                        CacheReadTokens: attributes.Tokens("cache_read_tokens"),
                        CacheCreationTokens: attributes.Tokens("cache_creation_tokens"),
                        CostUsdMicros: costMicros,
                        DurationMs: attributes.Tokens("duration_ms"),
                        EventKey: attributes.Text("event.id") is { } eventId ? UsageIdentity.Hash(eventId) : null,
                        CostSource: costMicros is null ? "unpriced" : "reported"));
                }
            }
        }

        return rows;
    }

    private static bool IsApiRequest(JsonElement record, Dictionary<string, JsonElement> attributes) =>
        attributes.Text("event.name") == "api_request" ||
        record.ObjectOrDefault("body") is { ValueKind: JsonValueKind.Object } body &&
        body.TryGetProperty("stringValue", out var name) && name.GetString() == "claude_code.api_request";

    private static DateTime ReadTime(JsonElement record, Dictionary<string, JsonElement> attributes)
    {
        foreach (var field in new[] { "timeUnixNano", "observedTimeUnixNano" })
        {
            if (record.TryGetProperty(field, out var value) && ParseLong(value) is long nanoseconds && nanoseconds > 0)
            {
                return ValidTime(DateTime.UnixEpoch.AddTicks(nanoseconds / 100));
            }
        }

        if (attributes.Text("event.timestamp") is { } timestamp &&
            DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return ValidTime(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
        }

        throw new InvalidDataException("API request timestamps are required.");
    }

    private static DateTime ValidTime(DateTime time)
    {
        if (time < DateTime.UnixEpoch || time > DateTime.UtcNow.AddMinutes(5))
        {
            throw new InvalidDataException("API request timestamp is out of range.");
        }

        return time;
    }

    // OTLP attributes contain key/value pairs whose values use stringValue, intValue, etc.
    private static Dictionary<string, JsonElement> ReadAttributes(JsonElement owner)
    {
        var attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var attribute in owner.ArrayOrEmpty("attributes"))
        {
            if (attribute.TryGetProperty("key", out var key) && attribute.TryGetProperty("value", out var value))
            {
                if (key.ValueKind != JsonValueKind.String || value.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Invalid OTLP attribute.");
                }

                foreach (var typedValue in value.EnumerateObject())
                {
                    attributes[key.GetString()!] = typedValue.Value;
                    break;
                }
            }
        }

        return attributes;
    }

    private static string? Text(this Dictionary<string, JsonElement> attributes, string key)
    {
        if (!attributes.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        if (text?.Length > 200)
        {
            throw new InvalidDataException("An attribution value is too long.");
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int Tokens(this Dictionary<string, JsonElement> attributes, string key)
    {
        var count = attributes.Integer(key) ?? 0;
        if (count < 0 || count > int.MaxValue)
        {
            throw new InvalidDataException("Invalid token or duration count.");
        }

        return (int)count;
    }

    // OTLP JSON encodes 64-bit integers as strings, but some exporters send numbers.
    private static long? Integer(this Dictionary<string, JsonElement> attributes, string key) =>
        attributes.TryGetValue(key, out var value) ? ParseLong(value) : null;

    private static double? Number(this Dictionary<string, JsonElement> attributes, string key)
    {
        if (!attributes.TryGetValue(key, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDouble();
        }

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException("Invalid OTLP number.");
    }

    private static long? ParseLong(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => (long)Math.Round(value.GetDouble()),
        JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => integer,
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => (long)Math.Round(number),
        _ => null,
    };

    private static IEnumerable<JsonElement> ArrayOrEmpty(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : [];

    private static JsonElement ObjectOrDefault(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value
            : default;
}
