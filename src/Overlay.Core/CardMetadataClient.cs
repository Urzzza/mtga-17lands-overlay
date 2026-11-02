using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace Overlay.Core;

public sealed record ArenaCard(int Id, string Set, string Name);

/// <summary>Uses authoritative Arena IDs/names from the licensed public card index, with MTGJSON mana costs.</summary>
public sealed class CardMetadataClient(HttpClient http, Storage storage)
{
    private Dictionary<int, ArenaCard>? index;
    private readonly Dictionary<string, Dictionary<string, string>> costs = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset nextIndexRefresh;
    public async Task<Dictionary<int, Card>> FetchAsync(IEnumerable<int> ids, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requested = ids.Distinct().Where(id => id > 0).ToArray();
        var result = new Dictionary<int, Card>();
        try
        {
            index ??= await LoadIndexAsync(false, progress, ct);
            if (DateTimeOffset.UtcNow >= nextIndexRefresh && requested.Any(id => !index.ContainsKey(id)))
            { nextIndexRefresh = DateTimeOffset.UtcNow.AddMinutes(1); index = await LoadIndexAsync(true, progress, ct); }
            foreach (var set in requested.Where(index.ContainsKey).Select(id => index[id].Set).Distinct())
            {
                Dictionary<string, string> setCosts = new();
                try
                {
                    if (costs.TryGetValue(set, out var cachedCosts)) setCosts = cachedCosts;
                    else costs[set] = setCosts = await LoadCostsAsync(set, progress, ct);
                }
                catch (Exception ex) when (Recoverable(ex, ct)) { progress?.Report($"Mana costs for {set} unavailable ({Reason(ex)}); card names and win rates remain usable."); }
                // Cache the whole set so subsequent draft picks do not require another request.
                foreach (var card in index.Values.Where(card => card.Set == set))
                    result[card.Id] = new(card.Id, card.Name, setCosts.TryGetValue(card.Name, out var cost) ? cost : "?");
            }
        }
        catch (Exception ex) when (Recoverable(ex, ct)) { progress?.Report($"Public Arena card index unavailable: {Reason(ex)}."); }
        var unresolved = requested.Where(id => !result.ContainsKey(id)).ToArray();
        if (unresolved.Length > 0)
        {
            progress?.Report($"Trying Scryfall Arena-ID lookups for {unresolved.Length} unmatched card(s).");
            foreach (var id in unresolved)
            {
                await Task.Delay(200, ct);
                try
                {
                    using var response = await GetAsync($"https://api.scryfall.com/cards/arena/{id}", ct);
                    if (response.StatusCode == HttpStatusCode.NotFound) continue;
                    response.EnsureSuccessStatusCode();
                    using var doc = await ReadJsonAsync(response, ct);
                    var card = Metadata.Parse(doc.RootElement);
                    if (card is not null && card.ArenaId == id) result[id] = card;
                }
                catch (Exception ex) when (Recoverable(ex, ct)) { progress?.Report($"Scryfall lookup unavailable: {Reason(ex)}."); break; }
            }
        }
        return result;
    }
    private async Task<Dictionary<int, ArenaCard>> LoadIndexAsync(bool force, IProgress<string>? progress, CancellationToken ct)
    {
        const string cache = "arena-card-index.json";
        var saved = ReadCache<Dictionary<int, ArenaCard>>(cache);
        if (saved is not null && saved.Any(p => p.Value is null || p.Key <= 0 || p.Value.Id != p.Key || string.IsNullOrWhiteSpace(p.Value.Name) || p.Value.Name.Length > 500 || string.IsNullOrWhiteSpace(p.Value.Set))) saved = null;
        if (!force && saved is not null && Fresh(cache)) return saved;
        try
        {
            nextIndexRefresh = DateTimeOffset.UtcNow.AddMinutes(1);
            progress?.Report("Downloading 17Lands public card index…");
            using var response = await GetAsync("https://17lands-public.s3.amazonaws.com/analysis_data/cards/cards.csv", ct);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(32 * 1024 * 1024, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var parsed = await Task.Run(() => ParseArenaIndex(stream, ct), ct);
            await storage.WriteAsync(cache, parsed, ct);
            progress?.Report($"Loaded {parsed.Count:N0} Arena card identities."); return parsed;
        }
        catch (Exception ex) when (saved is not null && Recoverable(ex, ct))
        { progress?.Report($"Using cached Arena card index ({Reason(ex)})."); return saved; }
    }
    private async Task<Dictionary<string, string>> LoadCostsAsync(string set, IProgress<string>? progress, CancellationToken ct)
    {
        Storage.ValidateKey(set, "PremierDraft"); string cache = $"mana-costs-{set}.json";
        var saved = ReadCache<Dictionary<string, string>>(cache);
        if (saved is not null && saved.Any(p => p.Key.Length > 500 || p.Value is null || p.Value.Length > 500)) saved = null;
        if (saved is not null && Fresh(cache)) return saved;
        try
        {
            progress?.Report($"Downloading {set} card mana costs from MTGJSON…");
            using var response = await GetAsync($"https://mtgjson.com/api/v5/{Uri.EscapeDataString(set)}.json", ct);
            response.EnsureSuccessStatusCode();
            using var doc = await ReadJsonAsync(response, ct);
            var parsed = ParseManaCosts(doc.RootElement);
            await storage.WriteAsync(cache, parsed, ct); return parsed;
        }
        catch (Exception ex) when (saved is not null && Recoverable(ex, ct))
        { progress?.Report($"Using cached {set} mana costs ({Reason(ex)})."); return saved; }
    }
    private bool Fresh(string file) => DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(storage.Root, file)) < TimeSpan.FromDays(1);
    private T? ReadCache<T>(string file)
    {
        try { return storage.Read<T>(file); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return default; }
    }
    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        // Buffering with a limit keeps the timeout active for both headers and the response body.
        var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        try { await response.Content.LoadIntoBufferAsync(32 * 1024 * 1024, timeout.Token); return response; }
        catch { response.Dispose(); throw; }
    }
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct) =>
        await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    private static bool Recoverable(Exception ex, CancellationToken ct) => ex is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException or ArgumentException or MalformedLineException
        || (ex is OperationCanceledException && !ct.IsCancellationRequested);
    private static string Reason(Exception ex) => ex is HttpRequestException { StatusCode: { } status } ? $"HTTP {(int)status}" : ex is OperationCanceledException ? "request timed out" : ex.GetType().Name;

    public static Dictionary<int, ArenaCard> ParseArenaIndex(Stream stream, CancellationToken ct)
    {
        using var csv = new TextFieldParser(stream) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        csv.SetDelimiters(","); var header = csv.ReadFields() ?? throw new InvalidDataException("Missing Arena card index header.");
        int id = Array.IndexOf(header, "id"), set = Array.IndexOf(header, "expansion"), name = Array.IndexOf(header, "name");
        if (id < 0 || set < 0 || name < 0) throw new InvalidDataException("Invalid Arena card index columns.");
        var result = new Dictionary<int, ArenaCard>();
        while (!csv.EndOfData)
        {
            ct.ThrowIfCancellationRequested(); var row = csv.ReadFields()!;
            if (row.Length != header.Length) throw new InvalidDataException("Invalid Arena card index row.");
            if (!int.TryParse(row[id], NumberStyles.None, CultureInfo.InvariantCulture, out int arena) || arena <= 0 || string.IsNullOrWhiteSpace(row[name]) || row[name].Length > 500) continue;
            string code = row[set].ToUpperInvariant();
            try { Storage.ValidateKey(code, "PremierDraft"); } catch (ArgumentException) { continue; }
            result[arena] = new(arena, code, row[name]);
        }
        if (result.Count == 0) throw new InvalidDataException("Arena card index contains no valid cards.");
        return result;
    }
    public static Dictionary<string, string> ParseManaCosts(JsonElement root)
    {
        var faces = new List<(string Name, string Face, string Side, string Cost)>();
        foreach (var card in root.GetProperty("data").GetProperty("cards").EnumerateArray())
        {
            string Text(string key) => card.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
            string name = Text("name"), face = Text("faceName"), cost = Text("manaCost"), side = Text("side");
            if (string.IsNullOrWhiteSpace(name) || name.Length > 500 || cost.Length > 500) continue;
            faces.Add((name, face, side, cost));
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in faces.GroupBy(f => f.Name))
        {
            var distinct = group.DistinctBy(f => (f.Face, f.Side)).OrderBy(f => f.Side, StringComparer.Ordinal).ToArray();
            string combined = string.Join(" // ", distinct.Select(f => f.Cost));
            result[group.Key] = combined;
            // 17Lands game CSV uses front-face names for split/adventure cards.
            foreach (var face in distinct.Where(f => f.Face.Length > 0)) result.TryAdd(face.Face, combined);
        }
        if (result.Count == 0) throw new InvalidDataException("Set metadata contains no valid cards.");
        return result;
    }
}
