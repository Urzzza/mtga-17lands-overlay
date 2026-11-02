using System.Text.Json;
using System.Text.RegularExpressions;

namespace Overlay.Core;

public sealed record Card(int ArenaId, string Name, string ManaCost);
public sealed record CardStat(long Games, long Wins) { public double? Rate => Games == 0 ? null : (double)Wins / Games; }
public sealed record Dataset(string Set, string Format, string Identity, string Source, DateTimeOffset ImportedAt,
    string? CoverageStart, string? CoverageEnd, Dictionary<string, CardStat> Cards);
public sealed record DraftPack(string DraftId, string? Set, string? Format, int Pack, int Pick, int[] Cards, bool Active);
public sealed record RankedCard(int ArenaId, string Name, string ManaCost, double? Rate, long Games);
public sealed class Settings
{
    public string LogPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Wizards Of The Coast", "MTGA", "Player.log");
    public string? SetOverride { get; set; }
    public string? FormatOverride { get; set; }
    public double Scale { get; set; } = 1;
    public double Opacity { get; set; } = .9;
    public double OffsetX { get; set; } = 16;
    public double OffsetY { get; set; } = 16;
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(LogPath)) throw new ArgumentException("Choose an absolute Arena log path.");
        if (!string.IsNullOrWhiteSpace(SetOverride)) Storage.ValidateKey(SetOverride, "PremierDraft");
        if (!string.IsNullOrWhiteSpace(FormatOverride) && !Storage.Formats.Contains(FormatOverride)) throw new ArgumentException("Unsupported format.");
        if (!double.IsFinite(Scale) || Scale is < .5 or > 2 || !double.IsFinite(Opacity) || Opacity is < .2 or > 1
            || !double.IsFinite(OffsetX) || OffsetX is < 0 or > 2000 || !double.IsFinite(OffsetY) || OffsetY is < 0 or > 2000)
            throw new ArgumentException("Scale must be 0.5–2, opacity 0.2–1, and offsets 0–2000.");
    }
}
public sealed class Storage(string root)
{
    public static readonly string[] Formats = ["PremierDraft", "TradDraft", "QuickDraft"];
    public string Root { get; } = root;
    public static void ValidateKey(string set, string format)
    {
        if (!Regex.IsMatch(set, "^[A-Za-z0-9]{2,12}$") || !Formats.Contains(format)) throw new ArgumentException("Use a set code and a supported draft format.");
    }
    public string DatasetPath(string set, string format) { ValidateKey(set, format); return Path.Combine(Root, $"stats-{set.ToUpperInvariant()}-{format}.json"); }
    public T? Read<T>(string name) => File.Exists(Path.Combine(Root, name)) ? JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(Root, name))) : default;
    public async Task WriteAsync<T>(string name, T data, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Root);
        var target = Path.Combine(Root, name);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(file, data, cancellationToken: ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, target, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public Dataset? LoadDataset(string? set, string? format)
    {
        if (set is null || format is null) return null;
        var path = DatasetPath(set, format);
        var data = Read<Dataset>(Path.GetFileName(path));
        if (data is null) return null;
        if (data.Set is null || data.Format is null || data.Cards is null || data.Cards.Any(p => p.Value is null || p.Value.Games < 0 || p.Value.Wins < 0 || p.Value.Wins > p.Value.Games))
            throw new InvalidDataException("Invalid statistics cache. Reimport its source dataset.");
        return data.Set.Equals(set, StringComparison.OrdinalIgnoreCase) && data.Format == format ? data : null;
    }
}
public static class Ranking
{
    public static RankedCard[] Rank(DraftPack pack, IReadOnlyDictionary<int, Card> cards, Dataset? stats) => pack.Cards.Select(id =>
    {
        cards.TryGetValue(id, out var card);
        CardStat? stat = null;
        if (card is not null && stats?.Set.Equals(pack.Set, StringComparison.OrdinalIgnoreCase) == true && stats.Format == pack.Format)
            stats.Cards.TryGetValue(card.Name, out stat);
        return new RankedCard(id, card?.Name ?? $"Unknown card #{id}", card?.ManaCost ?? "?", stat?.Rate, stat?.Games ?? 0);
    }).OrderByDescending(c => c.Rate.HasValue).ThenByDescending(c => c.Rate).ThenBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.ArenaId).ToArray();
}
