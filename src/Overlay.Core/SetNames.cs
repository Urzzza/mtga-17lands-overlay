using System.Text.Json;
using System.Text.RegularExpressions;

namespace Overlay.Core;

public sealed record SetOption(string Code, string Name)
{
    public string Display => Name == Code ? Code : $"{Name} ({Code})";
}
public static class SetNames
{
    public static Dictionary<string, string> Bundled()
    {
        using var stream = typeof(SetNames).Assembly.GetManifestResourceStream("Overlay.Core.SetNames.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
    public static string Name(string code, IReadOnlyDictionary<string, string> names) => names.TryGetValue(code.ToUpperInvariant(), out var name) ? name : code;
    public static Dictionary<string, string> Parse(JsonElement root)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in root.GetProperty("data").EnumerateArray())
        {
            if (set.ValueKind != JsonValueKind.Object || !set.TryGetProperty("code", out var c) || c.ValueKind != JsonValueKind.String || !set.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
            string code = c.GetString()!, name = n.GetString()!.Trim();
            if (Regex.IsMatch(code, "^[A-Za-z0-9]{2,12}$") && name.Length is > 0 and <= 200 && !name.Any(char.IsControl)) names[code.ToUpperInvariant()] = name;
        }
        if (names.Count == 0) throw new InvalidDataException("No valid set names in metadata.");
        return names;
    }
    public static async Task<Dictionary<string, string>> FetchAsync(HttpClient http, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await http.GetAsync("https://mtgjson.com/api/v5/SetList.json", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(32 * 1024 * 1024, timeout.Token);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        return Parse(doc.RootElement);
    }
}
