using System.Net;
using System.Text;
using System.Text.Json;
using Overlay.Core;

internal static class CardMetadataTests
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        const string index = "id,expansion,name,types\n106231,FRA,Fateshaper Aspirant,Creature\n106391,FRA,Stingerquill Voxmancer,Creature\n106532,FRA,Mountain,Land\n";
        const string costs = """
            {"data":{"cards":[
            {"name":"Fateshaper Aspirant","manaCost":"{4}{W}"},
            {"name":"Mountain","types":["Land"]},
            {"name":"Stingerquill Voxmancer // Vicious Verse","faceName":"Stingerquill Voxmancer","side":"a","manaCost":"{B/R}"},
            {"name":"Stingerquill Voxmancer // Vicious Verse","faceName":"Vicious Verse","side":"b","manaCost":"{B/R}"}]}}
            """;
        int requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            requests++;
            if (request.RequestUri!.Host == "17lands-public.s3.amazonaws.com") return Response(index);
            if (request.RequestUri.AbsoluteUri == "https://mtgjson.com/api/v5/FRA.json") return Response(costs);
            throw new Exception("Unexpected metadata request: " + request.RequestUri);
        }));
        var storage = new Storage(Path.Combine(root, "metadata"));
        var client = new CardMetadataClient(http, storage);
        var cards = await client.FetchAsync([106231, 106391, 106532], null, default);
        check(cards[106231] == new Card(106231, "Fateshaper Aspirant", "{4}{W}"), "public index and MTGJSON resolve Arena card name and mana cost");
        check(cards[106391].Name == "Stingerquill Voxmancer" && cards[106391].ManaCost == "{B/R} // {B/R}", "front-face stats name retained with both face costs");
        check(cards[106532].ManaCost == "", "land has genuinely empty mana cost");
        await client.FetchAsync([106231], null, default);
        check(requests == 2, "whole-set metadata reused without per-pick network calls");
        await new CardMetadataClient(http, storage).FetchAsync([106391], null, default);
        check(requests == 2, "metadata survives restart through disk cache");
        var stats = new Dataset("FRA", "PremierDraft", "test", "test", DateTimeOffset.UtcNow, null, null, new() { ["Stingerquill Voxmancer"] = new(100, 60) });
        check(Ranking.Rank(new("x", "FRA", "QuickDraft", 1, 1, [106391], true), cards, stats)[0].Rate == .6, "public front-face identity joins Quick Draft fallback statistics");
        var scryCards = new Dictionary<int, Card> { [106391] = new(106391, "Stingerquill Voxmancer // Vicious Verse", "{B/R} // {B/R}") };
        check(Ranking.Rank(new("x", "FRA", "QuickDraft", 1, 1, [106391], true), scryCards, stats)[0].Rate == .6, "existing Scryfall full names match front-face statistics");
        using var failedCosts = new HttpClient(new Handler(request => request.RequestUri!.Host == "17lands-public.s3.amazonaws.com" ? Response(index) : new(HttpStatusCode.ServiceUnavailable)));
        var partial = await new CardMetadataClient(failedCosts, new Storage(Path.Combine(root, "partial-metadata"))).FetchAsync([106231], null, default);
        check(partial[106231].Name == "Fateshaper Aspirant" && partial[106231].ManaCost == "?", "mana-cost failure preserves usable names without pretending costs are zero");
        using var fallback = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "17lands-public.s3.amazonaws.com") return new(HttpStatusCode.ServiceUnavailable);
            check(request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/cards/arena/106231", "Scryfall fallback uses supported Arena-ID GET endpoint");
            return Response("{\"arena_id\":106231,\"name\":\"Fateshaper Aspirant\",\"mana_cost\":\"{4}{W}\"}");
        }));
        check((await new CardMetadataClient(fallback, new Storage(Path.Combine(root, "fallback-metadata"))).FetchAsync([106231], null, default)).ContainsKey(106231), "Scryfall can recover missing public-index data");
        File.SetLastWriteTimeUtc(Path.Combine(storage.Root, "arena-card-index.json"), DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(Path.Combine(storage.Root, "mana-costs-FRA.json"), DateTime.UtcNow.AddDays(-2));
        using var offline = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
        var retained = await new CardMetadataClient(offline, storage).FetchAsync([106231,106391], null, default);
        check(retained[106231].ManaCost == "{4}{W}" && retained[106391].Name == "Stingerquill Voxmancer", "expired metadata remains usable offline when refresh fails");
        var notices = new List<string>();
        using var denied = new HttpClient(new Handler(_ => new(HttpStatusCode.Forbidden)));
        var unresolved = await new CardMetadataClient(denied, new Storage(Path.Combine(root, "denied-metadata"))).FetchAsync([106231], new Capture(notices), default);
        check(unresolved.Count == 0 && notices.Any(n => n.Contains("HTTP 403")), "metadata failures log HTTP status without response bodies");
        try { await client.FetchAsync([106231], null, new CancellationToken(true)); throw new Exception("Metadata cancellation ignored"); }
        catch (OperationCanceledException) { check(true, "metadata cancellation propagates"); }
    }
    private sealed class Capture(List<string> messages) : IProgress<string> { public void Report(string value) => messages.Add(value); }
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(handle(request)); }
    }
}
