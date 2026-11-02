using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualBasic.FileIO;

namespace Overlay.Core;

public static class StatisticsImporter
{
    public static async Task<Dataset> ImportAsync(string path, string set, string format, IProgress<long>? progress, CancellationToken ct)
    {
        Storage.ValidateKey(set, format);
        // Hash the source, rather than its filename, to identify repeated imports.
        await using var file = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
        file.Position = 0;
        using Stream input = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress, true) : file;
        using var csv = new TextFieldParser(input) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        csv.SetDelimiters(",");
        var header = csv.ReadFields() ?? throw new InvalidDataException("Missing CSV header.");
        int won = Array.IndexOf(header, "won"), date = Array.IndexOf(header, "game_time");
        if (won < 0) throw new InvalidDataException("Expected 17Lands game data with a 'won' column.");
        var columns = header.Select((name, i) => (name, i)).Where(x => x.name.StartsWith("opening_hand_", StringComparison.Ordinal) || x.name.StartsWith("drawn_", StringComparison.Ordinal))
            .GroupBy(x => x.name[(x.name.StartsWith("drawn_", StringComparison.Ordinal) ? 6 : 13)..], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.i).ToArray(), StringComparer.Ordinal);
        if (columns.Count == 0) throw new InvalidDataException("No opening_hand_ or drawn_ card columns found.");
        var counts = columns.Keys.ToDictionary(k => k, _ => new CardStat(0, 0), StringComparer.Ordinal);
        long rows = 0;
        DateTimeOffset? first = null, last = null;
        while (!csv.EndOfData)
        {
            ct.ThrowIfCancellationRequested();
            var row = csv.ReadFields()!;
            if (row.Length != header.Length) throw new InvalidDataException($"Wrong column count at game {rows + 1}.");
            bool win = row[won].ToLowerInvariant() switch { "true" or "1" => true, "false" or "0" => false, _ => throw new InvalidDataException("Invalid won value.") };
            foreach (var (name, indexes) in columns)
            {
                bool seen = false;
                foreach (var i in indexes)
                {
                    if (!int.TryParse(row[i], NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 0) throw new InvalidDataException($"Invalid card count at game {rows + 1}.");
                    seen |= n > 0;
                }
                if (seen) { var old = counts[name]; counts[name] = new(old.Games + 1, old.Wins + (win ? 1 : 0)); }
            }
            if (date >= 0 && DateTimeOffset.TryParse(row[date], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
            { if (first is null || time < first) first = time; if (last is null || time > last) last = time; }
            if (++rows % 1000 == 0) progress?.Report(rows);
        }
        if (rows == 0) throw new InvalidDataException("Dataset contains no games.");
        progress?.Report(rows);
        return new(set.ToUpperInvariant(), format, hash, Path.GetFileName(path), DateTimeOffset.UtcNow, first?.ToString("yyyy-MM-dd"), last?.ToString("yyyy-MM-dd"), counts);
    }
}
