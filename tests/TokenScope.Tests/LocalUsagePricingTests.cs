using System.Text.Json;
using TokenScope.Api.Shared.Usage;
using Xunit;

namespace TokenScope.Tests;

public class LocalUsagePricingTests
{
    [Theory]
    [InlineData("claude-opus-5-5", 9360L)]
    [InlineData("claude-fable-5-1", 22400L)]
    public void Current_claude_cache_categories_use_model_specific_rates(string model, long expected)
    {
        using var doc = JsonDocument.Parse("""
            {"inference_geo":"not_available","cache_creation":{"ephemeral_5m_input_tokens":80,"ephemeral_1h_input_tokens":20}}
            """);
        var usage = doc.RootElement;
        Assert.Null(ClaudePricing.Price(usage, usage, usage, model, 1000, 200, 4000, 100).Cost);
        var priced = ClaudePricing.Price(usage, usage, usage, model, 1000, 200, 4000, 100, assumeStandardApiRates: true);
        Assert.Equal(expected, priced.Cost);
        Assert.Equal(ClaudePricing.AssumedSource, priced.Source);
    }

    [Fact]
    public void Unknown_models_and_explicit_fast_mode_remain_unpriced()
    {
        using var doc = JsonDocument.Parse("""{"speed":"fast"}""");
        var p = doc.RootElement;
        Assert.Null(ClaudePricing.Price(p, p, p, "claude-opus-5-5", 1000, 100, 0, 0, true).Cost);
        Assert.Null(ClaudePricing.Price(p, p, p, "claude-unknown", 1000, 100, 0, 0, true).Cost);
    }

}
