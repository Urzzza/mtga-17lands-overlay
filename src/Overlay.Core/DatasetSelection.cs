namespace Overlay.Core;

public sealed record DatasetRequest(string Set, string RequestedFormat, PublicDataset? Source)
{
    public bool IsFallback => RequestedFormat == "QuickDraft" && Source?.Format == "PremierDraft";
}
public static class DatasetSelection
{
    public static DatasetRequest[] Plan(IEnumerable<PublicDataset> catalog, IEnumerable<string> sets, IEnumerable<string> formats)
    {
        var published = catalog.ToArray(); var selectedFormats = formats.Distinct().ToArray();
        return sets.Select(s => s.ToUpperInvariant()).Distinct().SelectMany(set => selectedFormats.Select(format =>
        {
            Storage.ValidateKey(set, format);
            var source = published.FirstOrDefault(e => e.Set.Equals(set, StringComparison.OrdinalIgnoreCase) && e.Format == format);
            if (source is null && format == "QuickDraft")
                source = published.FirstOrDefault(e => e.Set.Equals(set, StringComparison.OrdinalIgnoreCase) && e.Format == "PremierDraft");
            return new DatasetRequest(set, format, source);
        })).ToArray();
    }
    public static PublicDataset[] Downloads(IEnumerable<DatasetRequest> requests) => requests.Where(r => r.Source is not null)
        .Select(r => r.Source!).DistinctBy(e => (e.Set, e.Format)).ToArray();
    public static bool CanUse(string? set, string? format, Dataset stats) => stats.Set.Equals(set, StringComparison.OrdinalIgnoreCase)
        && (stats.Format == format || (format == "QuickDraft" && stats.Format == "PremierDraft"));
}
