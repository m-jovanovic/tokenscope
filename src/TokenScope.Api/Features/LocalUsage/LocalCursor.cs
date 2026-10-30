namespace TokenScope.Api.Features.LocalUsage;

public sealed record LocalCursor
{
    public string Source { get; init; } = "";
    public string PathHash { get; init; } = "";
    public long Offset
    {
        get; init;
    }
    public long Length
    {
        get; init;
    }
    public DateTime LastWriteUtc
    {
        get; init;
    }
    public int PrefixLength
    {
        get; init;
    }
    public string PrefixHash { get; init; } = "";
    public string AnchorHash { get; init; } = "";
    public long Inserted
    {
        get; init;
    }
    public long Updated
    {
        get; init;
    }
    public long SkippedRecords
    {
        get; init;
    }
    public DateTime? LastPersistedAt
    {
        get; init;
    }
}
