using TokenScope.Api.Shared.Usage;

namespace TokenScope.Api.Features.History;

// Preview and import return the same coverage report; import also fills the two persistence counts.
public sealed record HistoryResponse(
    string PreviewId,
    DateTime ExpiresAt,
    int Records,
    int Requests,
    int Duplicates,
    int Skipped,
    bool Synthetic,
    string Source,
    string[] UnknownModels,
    long EstimatedCostUsdMicros,
    int PricedRequests,
    int UnpricedRequests,
    string PriceSource,
    IReadOnlyList<string> Issues,
    int? Inserted,
    int? AlreadyImported)
{
    public static HistoryResponse From(HistoryPreviews.Preview preview, int? inserted = null)
    {
        var data = preview.Data;
        var unknownModels = data.Rows
            .Where(row => !ClaudePricing.Knows(row.Model))
            .Select(row => row.Model)
            .Distinct()
            .ToArray();

        return new HistoryResponse(
            PreviewId: preview.Id,
            ExpiresAt: preview.ExpiresAt,
            Records: data.Records,
            Requests: data.Rows.Count,
            Duplicates: data.Duplicates,
            Skipped: data.Skipped,
            Synthetic: data.Synthetic,
            Source: "history",
            UnknownModels: unknownModels,
            EstimatedCostUsdMicros: data.Rows.Sum(row => row.CostUsdMicros ?? 0),
            PricedRequests: data.Rows.Count(row => row.CostUsdMicros is not null),
            UnpricedRequests: data.Rows.Count(row => row.CostUsdMicros is null),
            PriceSource: ClaudePricing.Source,
            Issues: data.Issues,
            Inserted: inserted,
            AlreadyImported: inserted is null ? null : data.Rows.Count - inserted);
    }
}
