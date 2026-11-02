using System.Net;
using System.Text.Json;

namespace Overlay.Core;

public sealed record PublicDataset(string Set, string Format, string Updated, Uri Url);
public sealed record DownloadSelection(string[] Sets, string[] Formats);
public sealed record DownloadProgress(string Stage, long Bytes = 0, long? TotalBytes = null, long Games = 0);

/// <summary>Uses the same public-data document as 17lands.com/public_datasets, never its curated statistics API.</summary>
public sealed class PublicDataClient(HttpClient http)
{
    private const string CatalogBase = "https://17lands.cdn.prismic.io/api/v2";
    private const long MaxDownloadBytes = 20L * 1024 * 1024 * 1024;
    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MTGA17LandsOverlay/0.2 (+https://github.com/Urzzza/mtga-17lands-overlay)");
        return client;
    }
    public async Task<PublicDataset[]> GetCatalogAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var repository = await GetJsonAsync(CatalogBase, timeout.Token);
        string? reference = repository.RootElement.GetProperty("refs").EnumerateArray()
            .Where(r => r.TryGetProperty("isMasterRef", out var master) && master.ValueKind == JsonValueKind.True)
            .Select(r => r.GetProperty("ref").GetString()).FirstOrDefault();
        if (string.IsNullOrEmpty(reference)) throw new InvalidDataException("17Lands public-data catalog has no current version.");
        var query = Uri.EscapeDataString("[[at(document.type,\"public-data\")]]");
        using var document = await GetJsonAsync($"{CatalogBase}/documents/search?ref={Uri.EscapeDataString(reference)}&q={query}&pageSize=100", timeout.Token);
        return ParseCatalog(document.RootElement);
    }
    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        // Catalog documents are small. Bound network buffering before parsing.
        using var buffer = new MemoryStream();
        await CopyAsync(stream, buffer, 8 * 1024 * 1024, null, null, ct);
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: ct);
    }
    public static PublicDataset[] ParseCatalog(JsonElement root)
    {
        var entries = new List<PublicDataset>();
        foreach (var document in root.GetProperty("results").EnumerateArray())
        foreach (var row in document.GetProperty("data").GetProperty("datasets").EnumerateArray())
        {
            string ReadText(string field) => string.Concat(row.GetProperty(field).EnumerateArray().Select(t => t.GetProperty("text").GetString())).Trim();
            string set = ReadText("expansion").ToUpperInvariant(), format = ReadText("format");
            try { Storage.ValidateKey(set, format); } catch (ArgumentException) { continue; }
            foreach (var block in row.GetProperty("game_data").EnumerateArray())
            {
                if (!block.TryGetProperty("spans", out var spans)) continue;
                foreach (var span in spans.EnumerateArray())
                {
                    if (span.GetProperty("type").GetString() != "hyperlink") continue;
                    if (!Uri.TryCreate(span.GetProperty("data").GetProperty("url").GetString(), UriKind.Absolute, out var url)) continue;
                    var entry = new PublicDataset(set, format, ReadText("last_updated"), url);
                    if (IsAllowed(entry)) entries.Add(entry);
                }
            }
        }
        var result = entries.GroupBy(e => (e.Set, e.Format)).Select(g => g.OrderByDescending(e => e.Updated, StringComparer.Ordinal).First())
            .OrderByDescending(e => e.Updated, StringComparer.Ordinal).ThenBy(e => e.Set).ThenBy(e => e.Format).ToArray();
        if (result.Length == 0) throw new InvalidDataException("No supported game datasets found in the public catalog. Manual CSV import is still available.");
        return result;
    }
    public static bool IsAllowed(PublicDataset entry)
    {
        try { Storage.ValidateKey(entry.Set, entry.Format); } catch (ArgumentException) { return false; }
        // Require the exact host and filename matching the selected set/format; never follow redirects.
        return entry.Url.IsAbsoluteUri && entry.Url.Scheme == "https" && entry.Url.IsDefaultPort && entry.Url.UserInfo.Length == 0
            && entry.Url.Host == "17lands-public.s3.amazonaws.com" && entry.Url.Query.Length == 0 && entry.Url.Fragment.Length == 0
            && entry.Url.AbsolutePath == $"/analysis_data/game_data/game_data_public.{entry.Set}.{entry.Format}.csv.gz";
    }
    public async Task DownloadAndImportAsync(PublicDataset entry, Storage storage, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!IsAllowed(entry)) throw new ArgumentException("Unsupported public dataset URL.");
        string directory = Path.Combine(storage.Root, "downloads"); Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".csv.gz");
        try
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = await http.GetAsync(entry.Url, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token);
            response.EnsureSuccessStatusCode();
            requestTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
            long? total = response.Content.Headers.ContentLength;
            if (total > MaxDownloadBytes) throw new InvalidDataException("Dataset exceeds the 20 GiB download limit. Use manual import instead.");
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await CopyAsync(source, destination, MaxDownloadBytes, total, progress, ct);
            progress?.Report(new("Importing"));
            var importProgress = progress is null ? null : new InlineProgress<long>(games => progress.Report(new("Importing", Games: games)));
            var dataset = await Task.Run(() => StatisticsImporter.ImportAsync(temp, entry.Set, entry.Format, importProgress, ct), ct);
            dataset = dataset with { Source = entry.Url.AbsoluteUri };
            await storage.WriteAsync(Path.GetFileName(storage.DatasetPath(entry.Set, entry.Format)), dataset, ct);
            progress?.Report(new("Complete"));
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static async Task CopyAsync(Stream source, Stream destination, long limit, long? total, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        byte[] buffer = new byte[65536]; long bytes = 0; long lastReport = 0;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct); idle.CancelAfter(TimeSpan.FromSeconds(60));
            int read = await source.ReadAsync(buffer, idle.Token);
            if (read == 0) break;
            bytes += read;
            if (bytes > limit) throw new InvalidDataException("Download exceeds the size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            if (bytes - lastReport >= 1024 * 1024) { progress?.Report(new("Downloading", bytes, total)); lastReport = bytes; }
        }
        if (total.HasValue && bytes != total.Value) throw new EndOfStreamException("Download ended before the dataset was complete.");
        progress?.Report(new("Downloading", bytes, total));
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
