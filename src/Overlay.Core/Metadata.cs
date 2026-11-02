using System.Text.Json;

namespace Overlay.Core;
public static class Metadata
{
    public static async Task<Dictionary<int, Card>> ImportAsync(Stream stream, CancellationToken ct)
    {
        var cards = new Dictionary<int, Card>();
        await foreach (var e in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream, cancellationToken: ct))
        {
            var card = Parse(e); if (card is not null) cards[card.ArenaId] = card;
        }
        if (cards.Count == 0) throw new InvalidDataException("Expected a Scryfall JSON array containing arena_id, name, and mana_cost or card_faces.");
        return cards;
    }
    public static Card? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("arena_id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int arena) || arena <= 0 || !e.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) return null;
        var name = n.GetString();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 500) return null;
        string cost = e.TryGetProperty("mana_cost", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
        if (cost.Length == 0 && e.TryGetProperty("card_faces", out var faces) && faces.ValueKind == JsonValueKind.Array)
            cost = string.Join(" // ", faces.EnumerateArray().Select(f => f.ValueKind == JsonValueKind.Object && f.TryGetProperty("mana_cost", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : ""));
        if (cost.Length > 500) return null;
        return new(arena, name, cost);
    }
}
