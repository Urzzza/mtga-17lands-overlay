using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Overlay.Core;

public static class StatisticsImporter
{
    // Parsing is CPU-bound. Keep it off the UI thread, including for local imports.
    public static Task<Dataset> ImportAsync(string path, string set, string format, IProgress<long>? progress, CancellationToken ct) =>
        Task.Run(() => Import(path, set, format, progress, ct), ct);

    private static Dataset Import(string path, string set, string format, IProgress<long>? progress, CancellationToken ct)
    {
        Storage.ValidateKey(set, format);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var hashed = new HashingStream(file, hash);
        using Stream input = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(hashed, CompressionMode.Decompress, true) : hashed;
        using var csv = new CsvRecords(input, ct);
        var headerRecord = csv.Read() ?? throw new InvalidDataException("Missing CSV header.");
        var headerFields = new CsvFields(headerRecord.AsSpan());
        var header = new List<string>();
        while (headerFields.MoveNext()) header.Add(headerFields.Value.ToString().Replace("\"\"", "\"", StringComparison.Ordinal));
        int won = header.IndexOf("won"), date = header.IndexOf("game_time");
        if (won < 0) throw new InvalidDataException("Expected 17Lands game data with a 'won' column.");
        var cardNames = new List<string>();
        var cardIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        // -1 marks an irrelevant field. Resolve column names once instead of doing dictionary work per game.
        var columnCard = Enumerable.Repeat(-1, header.Count).ToArray();
        for (int i = 0; i < header.Count; i++)
        {
            string field = header[i]; int prefix = field.StartsWith("opening_hand_", StringComparison.Ordinal) ? 13 : field.StartsWith("drawn_", StringComparison.Ordinal) ? 6 : 0;
            if (prefix == 0) continue;
            string name = field[prefix..];
            if (!cardIndexes.TryGetValue(name, out int card)) { card = cardNames.Count; cardNames.Add(name); cardIndexes.Add(name, card); }
            columnCard[i] = card;
        }
        if (cardNames.Count == 0) throw new InvalidDataException("No opening_hand_ or drawn_ card columns found.");
        long[] games = new long[cardNames.Count], wins = new long[cardNames.Count], seenAt = new long[cardNames.Count];
        int[] seen = new int[cardNames.Count];
        long rows = 0; DateTimeOffset? first = null, last = null;
        var reportTimer = System.Diagnostics.Stopwatch.StartNew();
        string? record;
        while ((record = csv.Read()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            int fieldIndex = 0, seenCount = 0; bool win = false; long rowNumber = rows + 1;
            var fields = new CsvFields(record.AsSpan());
            while (fields.MoveNext())
            {
                if (fieldIndex >= header.Count) throw new InvalidDataException($"Wrong column count at game {rowNumber}.");
                var value = fields.Value;
                if (fieldIndex == won)
                {
                    if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.SequenceEqual("1")) win = true;
                    else if (!value.Equals("false", StringComparison.OrdinalIgnoreCase) && !value.SequenceEqual("0")) throw new InvalidDataException("Invalid won value.");
                }
                else if (fieldIndex == date)
                {
                    if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
                    { if (first is null || time < first) first = time; if (last is null || time > last) last = time; }
                }
                else if (columnCard[fieldIndex] is int card && card >= 0)
                {
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n)) throw new InvalidDataException($"Invalid card count at game {rowNumber}.");
                    // A generation stamp counts the card once even if multiple copies appeared in both zones.
                    if (n > 0 && seenAt[card] != rowNumber) { seenAt[card] = rowNumber; seen[seenCount++] = card; }
                }
                fieldIndex++;
            }
            if (fieldIndex != header.Count) throw new InvalidDataException($"Wrong column count at game {rowNumber}.");
            for (int i = 0; i < seenCount; i++) { games[seen[i]]++; if (win) wins[seen[i]]++; }
            rows++;
            if (rows % 1000 == 0 && reportTimer.ElapsedMilliseconds >= 200) { progress?.Report(rows); reportTimer.Restart(); }
        }
        if (rows == 0) throw new InvalidDataException("Dataset contains no games.");
        // Include any bytes beyond the decompressor's read-ahead so identity is SHA256 of the exact source file.
        byte[] remaining = new byte[65536];
        while (hashed.Read(remaining) > 0) ct.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();
        var counts = new Dictionary<string, CardStat>(cardNames.Count, StringComparer.Ordinal);
        for (int i = 0; i < cardNames.Count; i++) counts.Add(cardNames[i], new(games[i], wins[i]));
        progress?.Report(rows);
        return new(set.ToUpperInvariant(), format, Convert.ToHexString(hash.GetHashAndReset()), Path.GetFileName(path), DateTimeOffset.UtcNow, first?.ToString("yyyy-MM-dd"), last?.ToString("yyyy-MM-dd"), counts);
    }

    private sealed class HashingStream(Stream source, IncrementalHash hash) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) { int count = source.Read(buffer); hash.AppendData(buffer[..count]); return count; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Bounded records prevent an unmatched quote in a corrupt file from buffering the entire dataset.
    private sealed class CsvRecords(Stream stream, CancellationToken ct) : IDisposable
    {
        private readonly StreamReader reader = new(stream, Encoding.UTF8, true, 65536, true);
        private const int MaxRecordLength = 16 * 1024 * 1024;
        public string? Read()
        {
            string? line;
            do { ct.ThrowIfCancellationRequested(); line = reader.ReadLine(); } while (line is not null && string.IsNullOrWhiteSpace(line));
            if (line is null) return null;
            if (line.Length > MaxRecordLength) throw new InvalidDataException("CSV record exceeds 16 MiB.");
            if (!line.Contains('"')) return line;
            bool quoted = false;
            foreach (char c in line) if (c == '"') quoted = !quoted;
            if (!quoted) return line;
            var record = new StringBuilder(line);
            do
            {
                ct.ThrowIfCancellationRequested();
                line = reader.ReadLine() ?? throw new InvalidDataException("Unclosed quoted CSV field.");
                if (record.Length + line.Length + 1 > MaxRecordLength) throw new InvalidDataException("CSV record exceeds 16 MiB.");
                record.Append('\n').Append(line);
                foreach (char c in line) if (c == '"') quoted = !quoted;
            } while (quoted);
            return record.ToString();
        }
        public void Dispose() => reader.Dispose();
    }

    private ref struct CsvFields(ReadOnlySpan<char> record)
    {
        private readonly ReadOnlySpan<char> record = record;
        private int position;
        public ReadOnlySpan<char> Value { get; private set; }
        public bool MoveNext()
        {
            if (position > record.Length) return false;
            int start = position, opening = start;
            while (opening < record.Length && record[opening] is ' ' or '\t') opening++;
            if (opening < record.Length && record[opening] == '"')
            {
                int end = opening + 1;
                while (true)
                {
                    int relative = record[end..].IndexOf('"');
                    if (relative < 0) throw new InvalidDataException("Unclosed quoted CSV field.");
                    end += relative;
                    if (end + 1 < record.Length && record[end + 1] == '"') { end += 2; continue; }
                    break;
                }
                Value = record[(opening + 1)..end];
                int next = end + 1;
                while (next < record.Length && record[next] is ' ' or '\t') next++;
                if (next < record.Length && record[next] != ',') throw new InvalidDataException("Unexpected text after quoted CSV field.");
                position = next + 1;
            }
            else
            {
                int relative = record[start..].IndexOf(',');
                int end = relative < 0 ? record.Length : start + relative;
                Value = record[start..end];
                // Quotes inside unquoted fields are malformed, even when that column is not used.
                if (Value.Contains('"')) throw new InvalidDataException("Unexpected quote in unquoted CSV field.");
                position = end + 1;
            }
            return true;
        }
    }
}
