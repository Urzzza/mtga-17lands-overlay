using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Overlay.Core;

var root = Path.Combine(Path.GetTempPath(), "mtga-overlay-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); Console.WriteLine("PASS " + name); passed++; }
void Feed(DraftReader r, string message) => r.Feed("[UnityCrossThreadLogger]" + message);
try
{
    var r = new DraftReader();
    Feed(r, "==> Event_Join {\"EventName\":\"PremierDraft_DSK_20240924\"}");
    Check(r.Current.Active && r.Current.Set == "DSK" && r.Current.Cards.Length == 0, "join waits for pack");
    Feed(r, "Draft.Notify {\"draftId\":\"one\",\"SelfPack\":1,\"SelfPick\":2,\"PackCards\":\"1,2,3\"}");
    Check(r.Current.Cards.SequenceEqual([1, 2, 3]) && r.Current.Format == "PremierDraft", "human pack");
    Feed(r, "==> EventPlayerDraftMakePick " + JsonSerializer.Serialize(new { request = "{\"DraftId\":\"one\",\"GrpIds\":[1],\"Pack\":1,\"Pick\":2}" }));
    Check(r.Current.Cards.Length == 0, "nested pick clears pack");
    Feed(r, "Draft.Notify {\"draftId\":\"one\",\"SelfPack\":1,\"SelfPick\":2,\"PackCards\":\"1,2,3\"}");
    Check(r.Current.Cards.Length == 0, "duplicate cannot resurrect picked pack");
    Feed(r, "Draft.Notify {\"draftId\":\"one\",\"SelfPack\":1,\"SelfPick\":3,");
    r.Feed("\"PackCards\":\"4,5\"}");
    Check(r.Current.Pick == 3 && r.Current.Cards.Length == 2, "multiline record");
    Feed(r, "==> EventPlayerDraftMakePick {\"DraftId\":\"one\",\"GrpIds\":[1],\"Pack\":1,\"Pick\":2}");
    Check(r.Current.Pick == 3 && r.Current.Cards.Length == 2, "late pick cannot clear newer pack");
    Feed(r, "Draft.Notify {broken}");
    Feed(r, "Draft.Notify {\"draftId\":\"one\",\"SelfPack\":1,\"SelfPick\":4,\"PackCards\":\"6\"}");
    Check(r.Current.Pick == 4, "malformed recovery");
    Feed(r, "<== Draft_CompleteDraft {}"); Check(!r.Current.Active && r.Current.Cards.Length == 0, "completion");
    r.Reset();
    Feed(r, "<== BotDraft_DraftStatus " + JsonSerializer.Serialize(new { payload = new { EventName = "QuickDraft_DSK_20240924", DraftStatus = "PickNext", DraftId = "bot", PackNumber = 0, PickNumber = 0, DraftPack = new[] { "1", "2" } } }));
    Check(r.Current.Pack == 1 && r.Current.Pick == 1 && r.Current.Format == "QuickDraft", "bot zero-based numbers");
    Feed(r, "<== BotDraft_DraftPick {\"DraftStatus\":\"Completed\",\"DraftPack\":[]}"); Check(!r.Current.Active, "bot completion");
    Feed(r, "==> Event_Join {\"EventName\":\"TradDraft_DSK_20240924\"}"); Check(r.Current.Format == "TradDraft", "traditional detection");
    Feed(r, "scene {\"fromSceneName\":\"Draft\",\"toSceneName\":\"Home\"}"); Check(!r.Current.Active, "exit draft");

    string log = Path.Combine(root, "Player.log"); var tailReader = new DraftReader(); var tail = new LogTailer(tailReader);
    string pack = "[UnityCrossThreadLogger]Draft.Notify {\"draftId\":\"tail\",\"SelfPack\":1,\"SelfPick\":2,\"PackCards\":\"1\"}";
    await File.WriteAllTextAsync(log, pack[..40]); await tail.PollAsync(log, default); Check(!tailReader.Current.Active, "partial log line");
    await File.AppendAllTextAsync(log, pack[40..] + "\n"); await tail.PollAsync(log, default); Check(tailReader.Current.Cards.SequenceEqual([1]), "partial append reassembly");
    await File.WriteAllTextAsync(log, "[UnityCrossThreadLogger]Event_Join {\"EventName\":\"PremierDraft_MH3_1\"}\n");
    await tail.PollAsync(log, default); Check(tailReader.Current.Set == "MH3" && tailReader.Current.Cards.Length == 0, "truncation reset");
    var fresh = new DraftReader(); await new LogTailer(fresh).PollAsync(log, default); Check(fresh.Current.Set == "MH3", "startup recovery");

    var races = new DraftReader();
    Feed(races, "Draft.Notify {\"draftId\":\"old\",\"SelfPack\":1,\"SelfPick\":2,\"PackCards\":\"1\"}");
    Feed(races, "==> Event_Join {\"EventName\":\"PremierDraft_DSK_20240924\"}");
    Feed(races, "Draft.Notify {\"draftId\":\"new\",\"SelfPack\":1,\"SelfPick\":2,\"PackCards\":\"2\"}");
    Feed(races, "Draft.Notify {\"draftId\":\"old\",\"SelfPack\":1,\"SelfPick\":3,\"PackCards\":\"3\"}");
    Check(races.Current.DraftId == "new" && races.Current.Cards.SequenceEqual([2]), "retired draft response ignored");
    Feed(races, "==> EventPlayerDraftMakePick"); races.Feed("{\"DraftId\":\"new\",\"GrpIds\":[2],\"Pack\":1,\"Pick\":2}");
    Check(races.Current.Cards.Length == 0, "logger context survives separate JSON line");

    var csv = "won,game_time,opening_hand_Alpha,drawn_Alpha,opening_hand_Beta,drawn_Beta,opening_hand_Zero,drawn_Zero\nTrue,2024-09-24 12:00:00,2,1,0,1,0,0\nFalse,2024-09-25 12:00:00,0,1,0,0,0,0\nTrue,2024-09-26 12:00:00,0,0,1,0,0,0\n";
    string path = Path.Combine(root, "games.csv"); await File.WriteAllTextAsync(path, csv);
    var data = await StatisticsImporter.ImportAsync(path, "DSK", "PremierDraft", null, default);
    Check(data.Cards["Alpha"] == new CardStat(2, 1), "GIH counts duplicates and both zones once");
    Check(data.Cards["Beta"].Rate == 1 && data.Cards["Zero"].Rate is null, "wins and zero sample");
    Check(data.CoverageStart == "2024-09-24" && data.CoverageEnd == "2024-09-26", "coverage dates");
    await using (var f = File.Create(path + ".gz")) await using (var gz = new GZipStream(f, CompressionMode.Compress)) await gz.WriteAsync(Encoding.UTF8.GetBytes(csv));
    var compressed = await StatisticsImporter.ImportAsync(path + ".gz", "DSK", "PremierDraft", null, default);
    Check(compressed.Cards["Alpha"] == data.Cards["Alpha"], "gzip import");
    try { await StatisticsImporter.ImportAsync(path, "DSK", "PremierDraft", null, new CancellationToken(true)); throw new Exception("Cancelled import accepted"); }
    catch (OperationCanceledException) { Check(true, "cancelled importer stops"); }
    var store = new Storage(root); string name = Path.GetFileName(store.DatasetPath("DSK", "PremierDraft"));
    await store.WriteAsync(name, data); await store.WriteAsync(name, data);
    Check(store.LoadDataset("DSK", "PremierDraft")!.Cards["Alpha"].Games == 2, "repeat import replaces");
    try { await store.WriteAsync(name, compressed, new CancellationToken(true)); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
    Check(store.LoadDataset("DSK", "PremierDraft")!.Identity == data.Identity && !Directory.GetFiles(root, "*.tmp").Any(), "cancel retains cache and removes temp");
    Check(store.LoadDataset("DSK", "QuickDraft") is null, "no format fallback");
    await File.WriteAllTextAsync(path, "won,opening_hand_Alpha\nTrue,nope\n");
    try { await StatisticsImporter.ImportAsync(path, "DSK", "PremierDraft", null, default); throw new Exception("Invalid accepted"); } catch (InvalidDataException) { Check(true, "invalid CSV rejected"); }
    await File.WriteAllTextAsync(path, "won,\"opening_hand_A, B\",\"drawn_A, B\"\nTrue,1,1\n");
    Check((await StatisticsImporter.ImportAsync(path, "DSK", "PremierDraft", null, default)).Cards["A, B"].Games == 1, "quoted CSV card name");

    using var ms = new MemoryStream(Encoding.UTF8.GetBytes("[{\"arena_id\":1,\"name\":\"Alpha\",\"mana_cost\":\"{W}\"},{\"arena_id\":2,\"name\":\"Beta\",\"card_faces\":[{\"mana_cost\":\"{U}\"},{\"mana_cost\":\"{R}\"}]}]"));
    var cards = await Metadata.ImportAsync(ms, default); Check(cards[2].ManaCost == "{U} // {R}", "split costs");
    var ranked = Ranking.Rank(new("x", "DSK", "PremierDraft", 1, 1, [1, 2, 3], true), cards, data);
    Check(ranked.Select(c => c.ArenaId).SequenceEqual([2, 1, 3]) && ranked[2].Rate is null, "descending ranking missing last");
    Check(Ranking.Rank(new("x", "DSK", "QuickDraft", 1, 1, [1], true), cards, data)[0].Rate is null, "ranking format mismatch");
    var tie = data with { Cards = new() { ["Alpha"] = new(2, 1), ["Beta"] = new(4, 2) } };
    Check(Ranking.Rank(new("x", "DSK", "PremierDraft", 1, 1, [2, 1], true), cards, tie)[0].Name == "Alpha", "alphabetical ties");
    try { Storage.ValidateKey("../escape", "PremierDraft"); throw new Exception("Traversal accepted"); } catch (ArgumentException) { Check(true, "invalid set rejected"); }
    var activity = new ActivityLog(3);
    activity.Write("first"); activity.Write("second"); activity.Write("third"); activity.Write("fourth");
    var logSnapshot = activity.Snapshot();
    Check(logSnapshot.Entries.Length == 3 && logSnapshot.Entries[0].Message == "second" && logSnapshot.Entries[^1].Sequence == 4, "activity log retains bounded ordered history");
    activity.Clear();
    Check(activity.Snapshot().Version > logSnapshot.Version && activity.Snapshot().Entries.Length == 0, "activity clear updates view version");
    activity.Write("line1\nline2\t" + new string('x', 1200));
    Check(activity.Snapshot().Entries[0].Message.Length == 1000 && !activity.Snapshot().Entries[0].Message.Any(char.IsControl), "activity messages are bounded single-line summaries");
    var concurrentLog = new ActivityLog(500);
    Parallel.For(0, 1000, i => concurrentLog.Write($"event {i}"));
    var concurrentSnapshot = concurrentLog.Snapshot();
    Check(concurrentSnapshot.Entries.Length == 500 && concurrentSnapshot.Entries.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(501, 500).Select(i => (long)i)), "concurrent activity writes preserve order and capacity");
    var setNames = SetNames.Bundled();
    Check(new SetOption("DSK", SetNames.Name("DSK", setNames)).Display == "Duskmourn: House of Horror (DSK)", "full set name and code in bundled offline labels");
    Check(SetNames.Name("mh3", setNames) == "Modern Horizons 3", "set names ignore code case");
    Check(new SetOption("NEW", SetNames.Name("NEW", setNames)).Display == "NEW", "unknown set falls back to code");
    using (var namesJson = JsonDocument.Parse("{\"data\":[{\"code\":\"dsk\",\"name\":\"Duskmourn: House of Horror\"},{\"code\":\"../bad\",\"name\":\"Ignored\"}]}"))
        Check(SetNames.Parse(namesJson.RootElement).Count == 1, "set metadata validates codes and normalizes keys");
    await ImporterTests.RunAsync(root, Check);
    await PublicDataTests.RunAsync(root, Check);
    if (args.Contains("--live-catalog"))
    {
        using var http = PublicDataClient.CreateHttpClient();
        var published = await new PublicDataClient(http).GetCatalogAsync(default);
        var liveNames = await SetNames.FetchAsync(http, default);
        Check(liveNames.ContainsKey("DSK"), "live MTGJSON set-name lookup");
        Check(published.Length > 0 && published.All(PublicDataClient.IsAllowed), $"live public catalog: {published.Length} supported set/format pairs");
    }
    Console.WriteLine($"{passed} checks passed.");
}
finally { Directory.Delete(root, true); }
