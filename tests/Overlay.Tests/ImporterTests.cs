using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Overlay.Core;

internal static class ImporterTests
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        string path = Path.Combine(root, "wide.csv");
        string[] names = ["Alpha", "Card, With Comma", "Card \"With Quotes\"", "Room // Other Room"];
        var expected = names.ToDictionary(n => n, _ => new CardStat(0, 0));
        string Quote(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
        var header = new[] { "notes", "won", "game_time" }.Concat(names.Select(n => "opening_hand_" + n)).Concat(names.Select(n => "drawn_" + n)).Append("unused");
        var csv = new StringBuilder(string.Join(',', header.Select(Quote)) + "\r\n");
        var random = new Random(42);
        for (int row = 0; row < 300; row++)
        {
            bool win = random.Next(2) == 0;
            var opening = names.Select(_ => random.Next(4)).ToArray();
            var drawn = names.Select(_ => random.Next(4)).ToArray();
            for (int card = 0; card < names.Length; card++)
                if (opening[card] + drawn[card] > 0) { var old = expected[names[card]]; expected[names[card]] = new(old.Games + 1, old.Wins + (win ? 1 : 0)); }
            var fields = new[] { row % 3 == 0 ? "multiline\r\ntext, and \"quotes\"" : "plain", win ? (row % 2 == 0 ? "TRUE" : "1") : (row % 2 == 0 ? "False" : "0"), "2026-10-01T12:00:00Z" }
                .Concat(opening.Select(n => n.ToString())).Concat(drawn.Select(n => n.ToString())).Append("");
            csv.AppendLine(string.Join(',', fields.Select(Quote)));
        }
        await File.WriteAllTextAsync(path, csv.ToString(), new UTF8Encoding(true));
        var actual = await StatisticsImporter.ImportAsync(path, "DSK", "PremierDraft", null, default);
        check(names.All(n => actual.Cards[n] == expected[n]), "randomized GIH equivalence with escaped quotes multiline fields BOM and reordered columns");
        check(actual.Identity == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))), "single-pass plain hash matches exact source bytes");
        await using (var output = File.Create(path + ".gz"))
        await using (var gzip = new GZipStream(output, CompressionMode.Compress)) await gzip.WriteAsync(await File.ReadAllBytesAsync(path));
        var zipped = await StatisticsImporter.ImportAsync(path + ".gz", "DSK", "PremierDraft", null, default);
        check(names.All(n => zipped.Cards[n] == actual.Cards[n]) && zipped.Identity == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path + ".gz"))), "single-pass gzip hash and statistics match source");
        await File.WriteAllTextAsync(path, "won,opening_hand_Alpha,unused\ntrue,1,\nfalse,0,\n");
        check((await StatisticsImporter.ImportAsync(path, "DSK", "PremierDraft", null, default)).Cards["Alpha"] == new CardStat(1, 1), "trailing empty CSV column");
        foreach (string bad in new[] { "true,1,extra,field", "true,-1,x", "true,2147483648,x", "true,\"1\"garbage,x", "true,1,\"unclosed", "true,not-a-number,x" })
        {
            await File.WriteAllTextAsync(path, "won,opening_hand_Alpha,unused\n" + bad + "\n");
            try { await StatisticsImporter.ImportAsync(path, "DSK", "PremierDraft", null, default); throw new Exception("Malformed CSV accepted: " + bad); }
            catch (InvalidDataException) { }
        }
        check(true, "optimized parser rejects malformed rows quotes negative counts and overflow");
    }
}
