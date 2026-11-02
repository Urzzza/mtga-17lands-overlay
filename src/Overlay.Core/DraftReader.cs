using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Overlay.Core;

public sealed class DraftReader
{
    public DraftPack Current { get; private set; } = new("", null, null, 0, 0, [], false);
    private readonly HashSet<string> retired = new(StringComparer.Ordinal);
    private string pending = "";
    private string context = "";
    public void Reset() { Current = new("", null, null, 0, 0, [], false); pending = ""; context = ""; retired.Clear(); }
    public void Feed(string line)
    {
        if (line.Contains("[UnityCrossThreadLogger]", StringComparison.Ordinal))
        { pending = ""; context = line; }
        int start = pending.Length == 0 ? line.IndexOf('{') : 0;
        if (start < 0) return;
        pending += line[start..];
        if (pending.Length > 4 * 1024 * 1024) { pending = ""; return; }
        try
        {
            using var doc = JsonDocument.Parse(pending, new JsonDocumentOptions { MaxDepth = 64 });
            Visit(doc.RootElement, context, 0);
            pending = "";
        }
        catch (JsonException) { /* A record may span multiple log lines. Next logger prefix resets malformed records. */ }
    }
    private static string? Text(JsonElement e, string key) => e.TryGetProperty(key, out var p) ? p.ToString() : null;
    private static int Number(JsonElement e, string key) => int.TryParse(Text(e, key), out var n) ? n : 0;
    private void Visit(JsonElement e, string ctx, int depth)
    {
        if (depth > 10) return;
        if (e.ValueKind == JsonValueKind.String)
        {
            var value = e.GetString();
            if (value?.StartsWith('{') == true || value?.StartsWith('[') == true)
                try { using var nested = JsonDocument.Parse(value); Visit(nested.RootElement, ctx, depth + 1); } catch (JsonException) { }
            return;
        }
        if (e.ValueKind == JsonValueKind.Array) { foreach (var item in e.EnumerateArray()) Visit(item, ctx, depth + 1); return; }
        if (e.ValueKind != JsonValueKind.Object) return;
        var eventName = Text(e, "InternalEventName") ?? Text(e, "EventName");
        if (eventName is not null)
        {
            var match = Regex.Match(eventName, @"^(PremierDraft|TradDraft|QuickDraft|BotDraft)_([A-Za-z0-9]+)(?:_|$)");
            if (match.Success)
                Current = Current with { Set = match.Groups[2].Value.ToUpperInvariant(), Format = match.Groups[1].Value == "BotDraft" ? "QuickDraft" : match.Groups[1].Value };
            else if (ctx.Contains("EventJoin") || ctx.Contains("Event_Join")) Current = new("", null, null, 0, 0, [], false);
            if (match.Success && (ctx.Contains("EventJoin") || ctx.Contains("Event_Join")))
            {
                if (Current.DraftId.Length > 0 && Current.DraftId != "bot") retired.Add(Current.DraftId);
                Current = Current with { DraftId = "", Cards = [], Pack = 0, Pick = 0, Active = true };
            }
        }
        var to = Text(e, "toSceneName"); var from = Text(e, "fromSceneName");
        if (to is not null && to.Contains("Draft", StringComparison.OrdinalIgnoreCase)) Current = Current with { Active = true };
        else if (from?.Contains("Draft", StringComparison.OrdinalIgnoreCase) == true && to is not null) Current = Current with { Active = false, Cards = [] };
        if (ctx.Contains("CompleteDraft") || ctx.Contains("Complete_Draft") || Text(e, "DraftStatus") is "Completed" or "Complete")
            Current = Current with { Active = false, Cards = [] };
        else if (e.TryGetProperty("PackCards", out var human) && (ctx.Contains("Draft.Notify") || e.TryGetProperty("draftId", out _)))
            SetPack(Text(e, "draftId") ?? "", Number(e, "SelfPack"), Number(e, "SelfPick"), Ids(human));
        else if (e.TryGetProperty("DraftPack", out var bot) && Text(e, "DraftStatus") is "PickNext" or "Draft.PickNext")
            SetPack(Text(e, "DraftId") ?? "bot", Number(e, "PackNumber") + 1, Number(e, "PickNumber") + 1, Ids(bot));
        else if ((ctx.Contains("MakePick") && (e.TryGetProperty("GrpIds", out _) || e.TryGetProperty("GrpId", out _))) ||
                 (ctx.Contains("DraftPick") && (e.TryGetProperty("CardId", out _) || e.TryGetProperty("CardIds", out _))))
            ClearPickedPack(e, ctx);
        foreach (var p in e.EnumerateObject()) Visit(p.Value, ctx, depth + 1);
    }
    private void ClearPickedPack(JsonElement e, string ctx)
    {
        var id = Text(e, "DraftId");
        if (id is not null && Current.DraftId.Length > 0 && id != Current.DraftId) return;
        bool bot = ctx.Contains("BotDraft");
        int pack = Number(e, bot ? "PackNumber" : "Pack") + (bot ? 1 : 0);
        int pick = Number(e, bot ? "PickNumber" : "Pick") + (bot ? 1 : 0);
        if (pack > 0 && pick > 0 && (pack != Current.Pack || pick != Current.Pick)) return;
        Current = Current with { Cards = [] };
    }
    private void SetPack(string id, int pack, int pick, int[] cards)
    {
        if (retired.Contains(id)) return;
        if (cards.Length == 0 || cards.Length > 40 || pack is < 1 or > 3 || pick is < 1 or > 40) return;
        if (Current.DraftId == id && (pack < Current.Pack || (pack == Current.Pack && pick <= Current.Pick))) return;
        if (Current.DraftId.Length > 0 && Current.DraftId != "bot" && Current.DraftId != id) retired.Add(Current.DraftId);
        Current = Current with { DraftId = id, Pack = pack, Pick = pick, Cards = cards, Active = true };
    }
    private static int[] Ids(JsonElement e)
    {
        IEnumerable<string> values = e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.ToString()) : e.ToString().Split(',');
        return values.Select(s => int.TryParse(s, out var id) ? id : 0).Where(id => id > 0).ToArray();
    }
}

public sealed class LogTailer(DraftReader reader)
{
    private long offset;
    private byte[] prefix = [];
    private DateTime creation;
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder line = new();
    public async Task PollAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true);
        var head = new byte[(int)Math.Min(128, file.Length)];
        await file.ReadExactlyAsync(head, ct);
        var created = File.GetCreationTimeUtc(path);
        bool reset = file.Length < offset || created != creation || !head.AsSpan(0, Math.Min(prefix.Length, head.Length)).SequenceEqual(prefix.AsSpan(0, Math.Min(prefix.Length, head.Length)));
        if (reset) { offset = 0; line.Clear(); decoder.Reset(); reader.Reset(); }
        creation = created; prefix = head;
        file.Position = offset;
        byte[] bytes = new byte[65536]; char[] chars = new char[65536];
        int read;
        while ((read = await file.ReadAsync(bytes, ct)) > 0)
        {
            offset += read;
            int count = decoder.GetChars(bytes, 0, read, chars, 0, false);
            for (int i = 0; i < count; i++)
                if (chars[i] == '\n') { reader.Feed(line.ToString().TrimEnd('\r')); line.Clear(); }
                else if (line.Length < 4 * 1024 * 1024) line.Append(chars[i]);
        }
    }
}
