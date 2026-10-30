namespace TokenScope.Api.Shared.Usage;

public static class UsageSources
{
    public const string ClaudeLocal = "claude-local";
    // Retired datasets stay in storage, but are not selectable or included in the source catalog.
    public static string[] Active => ["live", ClaudeLocal, "history", "demo"];
    public static bool IsActive(string source) => source is "live" or ClaudeLocal or "history" or "demo";
}
