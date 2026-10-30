using TokenScope.Api.Shared.Usage;

namespace TokenScope.Api.Features.History;

public sealed record HistoryParseResult(
    int Records,
    int Duplicates,
    int Skipped,
    bool Synthetic,
    IReadOnlyList<ApiRequestRow> Rows,
    IReadOnlyList<string> Issues);
