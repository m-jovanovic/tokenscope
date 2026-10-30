namespace TokenScope.Api.Features.LocalUsage;

public sealed class LocalUsageOptions
{
    public bool Enabled
    {
        get; set;
    }
    public string ClaudePath { get; set; } = "";
}
