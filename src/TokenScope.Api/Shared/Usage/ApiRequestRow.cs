namespace TokenScope.Api.Shared.Usage;

// One normalized usage record. Live telemetry, imported history, local collection, and generated data stay separate.
public sealed record ApiRequestRow(
    DateTime Time,
    string Source,
    string Developer,
    string SessionId,
    string Model,
    string? QuerySource,
    string? AgentName,
    string? SkillName,
    string? McpServer,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheCreationTokens,
    long? CostUsdMicros,
    int? DurationMs,
    string? EventKey = null,
    string CostSource = "reported",
    bool IsSynthetic = false);
