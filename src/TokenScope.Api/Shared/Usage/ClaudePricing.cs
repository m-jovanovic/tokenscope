using System.Text.Json;

namespace TokenScope.Api.Shared.Usage;

public static class ClaudePricing
{
    public const string Source = "claude-api-standard-2026-10-21; https://platform.claude.com/docs/en/about-claude/pricing";
    public const string AssumedSource = "claude-api-standard-assumed-2026-10-21";

    // Deliberately small, exact allowlist. No guessed pricing for future model names.
    private static readonly Dictionary<string, (decimal Input, decimal Output, decimal Read, bool LongContext)> Rates = new()
    {
        ["claude-haiku-4-5"] = (1m, 5m, .1m, false),
        ["claude-haiku-4-5-20251001"] = (1m, 5m, .1m, false),
        ["claude-sonnet-4-5"] = (3m, 15m, .3m, false),
        ["claude-sonnet-4-5-20250929"] = (3m, 15m, .3m, false),
        ["claude-opus-4-5"] = (5m, 25m, .5m, false),
        ["claude-opus-4-5-20251101"] = (5m, 25m, .5m, false),
        ["claude-opus-4-6"] = (5m, 25m, .5m, true),
        ["claude-opus-4-7"] = (5m, 25m, .5m, true),
        ["claude-opus-4-8"] = (5m, 25m, .5m, true),
        ["claude-opus-5"] = (5m, 25m, .5m, true),
        ["claude-opus-5-5"] = (4m, 20m, .2m, true),
        ["claude-fable-5"] = (10m, 50m, 1m, true),
        ["claude-fable-5-1"] = (10m, 50m, .25m, true),
        ["claude-sonnet-4-6"] = (3m, 15m, .3m, true),
        ["claude-sonnet-5"] = (2m, 10m, .2m, true),
        ["claude-sonnet-5-5"] = (2m, 10m, .1m, true),
    };

    public static bool Knows(string model) => Rates.ContainsKey(model);

    public static (long? Cost, string Source) Price(
        JsonElement root,
        JsonElement message,
        JsonElement usage,
        string model,
        int inputTokens,
        int outputTokens,
        int cacheReadTokens,
        int cacheCreationTokens,
        bool assumeStandardApiRates = false)
    {
        foreach (var owner in new[] { root, message, usage })
        {
            foreach (var key in new[] { "costUSD", "cost_usd", "cost_usd_micros" })
            {
                if (owner.TryGetProperty(key, out var reportedCost) &&
                    reportedCost.ValueKind == JsonValueKind.Number &&
                    reportedCost.TryGetDecimal(out var value) && value >= 0 &&
                    value < (key == "cost_usd_micros" ? 1_000_000_000_000m : 1_000_000m))
                {
                    var costMicros = key == "cost_usd_micros" ? value : value * 1_000_000m;
                    return ((long)decimal.Round(costMicros), "reported-in-jsonl");
                }
            }
        }

        if (!Rates.TryGetValue(model, out var rate))
        {
            return (null, "unpriced:unknown-model");
        }

        var contextTokens = (long)inputTokens + cacheReadTokens + cacheCreationTokens;
        var contextLimit = rate.LongContext ? 1_000_000 : 200_000;
        if (contextTokens > contextLimit)
        {
            return (null, "unpriced:long-context");
        }

        // This rate card covers standard first-party/global API token costs only.
        foreach (var owner in new[] { root, message, usage })
        {
            foreach (var key in new[] { "speed", "service_tier", "inference_geo", "provider" })
            {
                if (owner.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
                    value.GetString() is { } setting && setting is not ("standard" or "global" or "anthropic") &&
                    !(assumeStandardApiRates && setting == "not_available"))
                {
                    return (null, "unpriced:unsupported-pricing-mode");
                }
            }
        }

        var fiveMinuteCacheTokens = 0;
        var oneHourCacheTokens = 0;
        if (cacheCreationTokens > 0)
        {
            if (!usage.TryGetProperty("cache_creation", out var cache) || cache.ValueKind != JsonValueKind.Object)
            {
                return (null, "unpriced:cache-duration-unknown");
            }

            fiveMinuteCacheTokens = UsageTokens.Read(cache, "ephemeral_5m_input_tokens");
            oneHourCacheTokens = UsageTokens.Read(cache, "ephemeral_1h_input_tokens");
            if ((long)fiveMinuteCacheTokens + oneHourCacheTokens != cacheCreationTokens)
            {
                return (null, "unpriced:cache-duration-unknown");
            }
        }

        // Dollars / million tokens equals microdollars / token.
        var estimatedMicros = inputTokens * rate.Input + outputTokens * rate.Output + cacheReadTokens * rate.Read
            + fiveMinuteCacheTokens * rate.Input * 1.25m + oneHourCacheTokens * rate.Input * 2m;
        return ((long)decimal.Round(estimatedMicros), assumeStandardApiRates ? AssumedSource : Source);
    }
}
