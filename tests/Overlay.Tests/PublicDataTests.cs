using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Overlay.Core;

internal static class PublicDataTests
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        string Url(string set, string format) => $"https://17lands-public.s3.amazonaws.com/analysis_data/game_data/game_data_public.{set}.{format}.csv.gz";
        object Row(string set, string format, string url, string updated = "2026-10-01") => new
        {
            expansion = new[] { new { text = set } }, format = new[] { new { text = format } }, last_updated = new[] { new { text = updated } },
            game_data = new[] { new { spans = new[] { new { type = "hyperlink", data = new { url } } } } }
        };
        string Catalog(params object[] rows) => JsonSerializer.Serialize(new { results = new[] { new { data = new { datasets = rows } } } });
        var catalogJson = Catalog(Row("DSK", "PremierDraft", Url("DSK", "PremierDraft")), Row("DSK", "PremierDraft", Url("DSK", "PremierDraft"), "2026-11-01"),
            Row("DSK", "QuickDraft", Url("DSK", "QuickDraft")), Row("MH3", "TradDraft", Url("MH3", "TradDraft")),
            Row("DSK", "Sealed", Url("DSK", "Sealed")), Row("DSK", "TradDraft", "https://attacker.example/data.csv.gz"));
        using var doc = JsonDocument.Parse(catalogJson);
        var catalog = PublicDataClient.ParseCatalog(doc.RootElement);
        check(catalog.Length == 3 && catalog.First(e => e.Format == "PremierDraft").Updated == "2026-11-01", "catalog supported pairs and newest version");
        var entry = catalog.First(e => e.Format == "PremierDraft");
        check(!PublicDataClient.IsAllowed(entry with { Url = new Uri("http://17lands-public.s3.amazonaws.com" + entry.Url.AbsolutePath) }) &&
              !PublicDataClient.IsAllowed(entry with { Url = new Uri(Url("MH3", "PremierDraft")) }) &&
              !PublicDataClient.IsAllowed(entry with { Url = new Uri(entry.Url + "?redirect=1") }), "download URL host scheme and pair validation");
        using var catalogHttp = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v2") return Json("{\"refs\":[{\"isMasterRef\":true,\"ref\":\"ref+with/slash=\"}]}");
            check(request.RequestUri.Query.Contains("ref%2Bwith%2Fslash%3D", StringComparison.OrdinalIgnoreCase), "catalog ref is URL encoded");
            return Json(catalogJson);
        }));
        check((await new PublicDataClient(catalogHttp).GetCatalogAsync(default)).Length == 3, "catalog discovery from published document");

        using var compressed = new MemoryStream();
        using (var gz = new GZipStream(compressed, CompressionMode.Compress, true)) gz.Write(Encoding.UTF8.GetBytes("won,game_time,opening_hand_Alpha,drawn_Alpha\nTrue,2026-10-01,1,1\nFalse,2026-10-02,0,1\n"));
        var payload = compressed.ToArray(); var storage = new Storage(Path.Combine(root, "download-tests"));
        using var goodHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var service = new PublicDataClient(goodHttp);
        await service.DownloadAndImportAsync(entry, storage, null, default);
        var imported = storage.LoadDataset("DSK", "PremierDraft")!;
        check(imported.Cards["Alpha"] == new CardStat(2, 1) && imported.Source == entry.Url.AbsoluteUri, "download automatically imports matching pair");
        await service.DownloadAndImportAsync(entry, storage, null, default);
        check(storage.LoadDataset("DSK", "PremierDraft")!.Cards["Alpha"].Games == 2, "download replacement does not double count");
        check(!Directory.EnumerateFiles(Path.Combine(storage.Root, "downloads")).Any(), "successful download cleans temporary file");
        using var badHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("not gzip") }));
        try { await new PublicDataClient(badHttp).DownloadAndImportAsync(entry, storage, null, default); throw new Exception("Bad gzip accepted"); }
        catch (InvalidDataException) { }
        check(storage.LoadDataset("DSK", "PremierDraft")!.Identity == imported.Identity && !Directory.EnumerateFiles(Path.Combine(storage.Root, "downloads")).Any(), "bad download preserves data and cleans temporary file");
        using var shortHttp = new HttpClient(new Handler(_ => { var content = new ByteArrayContent(payload); content.Headers.ContentLength = payload.Length + 10; return new(HttpStatusCode.OK) { Content = content }; }));
        try { await new PublicDataClient(shortHttp).DownloadAndImportAsync(entry, storage, null, default); throw new Exception("Truncated download accepted"); }
        catch (EndOfStreamException) { check(true, "truncated download rejected"); }
        using var redirectHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://attacker.example/file") } }));
        try { await new PublicDataClient(redirectHttp).DownloadAndImportAsync(entry, storage, null, default); throw new Exception("Redirect accepted"); }
        catch (HttpRequestException) { check(true, "redirect is not treated as a dataset"); }
        try { await service.DownloadAndImportAsync(entry, storage, null, new CancellationToken(true)); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        check(storage.LoadDataset("DSK", "PremierDraft")!.Identity == imported.Identity && !Directory.EnumerateFiles(Path.Combine(storage.Root, "downloads")).Any(), "cancelled download preserves data and removes temporary file");
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(handle(request)); }
    }
}
