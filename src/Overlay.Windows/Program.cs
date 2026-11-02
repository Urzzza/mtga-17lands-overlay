using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Overlay.Core;
using Forms = System.Windows.Forms;

namespace Overlay.Windows;
internal sealed class Program : Application
{
    internal readonly Storage Store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MTGA17LandsOverlay"));
    internal Settings Settings = new();
    internal readonly ActivityLog Activity = new();
    private readonly DispatcherTimer arenaTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ArenaPresence arenaPresence = new();
    private string lastDraft = "";
    private bool processDetectionUnavailable;
    private bool logUnavailable;
    private string? status;
    private System.Drawing.Icon? trayIcon;
    private Dictionary<int, Card> cards = new();
    private readonly Dictionary<int, DateTimeOffset> attempted = new();
    private readonly System.Net.Http.HttpClient metadataHttp = PublicDataClient.CreateHttpClient();
    private CardMetadataClient metadataClient = null!;
    private readonly OverlayWindow overlay = new();
    private readonly DraftReader reader = new();
    private LogTailer tailer = null!;
    private Forms.NotifyIcon tray = null!;
    private readonly CancellationTokenSource shutdown = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool busy, hidden, preview;
    private Forms.Screen previewScreen = Forms.Screen.PrimaryScreen!;
    private string tailPath = "";
    private bool fetching;
    private readonly SemaphoreSlim cardGate = new(1, 1);
    private readonly bool probe = Environment.GetCommandLineArgs().Contains("--fullscreen-probe");
    internal string? Status
    {
        get => status;
        set { if (status == value) return; status = value; if (!string.IsNullOrEmpty(value)) Activity.Write(value); }
    }
    private SettingsWindow? settingsWindow;
    [STAThread] public static void Main() { new Program().Run(); }
    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Activity.Write("MTGA 17Lands Overlay started.");
        try { Settings = Store.Read<Settings>("settings.json") ?? new(); Settings.Validate(); cards = Store.Read<Dictionary<int, Card>>("cards.json") ?? new(); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or ArgumentException) { Status = "Settings/cache could not be loaded. Open settings to repair."; Settings = new(); }
        tailer = new(reader); metadataClient = new(metadataHttp, Store);
        tray = new Forms.NotifyIcon { Icon = trayIcon = AppIcon.Tray(), Text = "MTGA 17Lands Overlay", Visible = true, ContextMenuStrip = new Forms.ContextMenuStrip() };
        tray.ContextMenuStrip.Items.Add("Settings / import data", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        tray.ContextMenuStrip.Items.Add("Preview", null, (_, _) => Dispatcher.Invoke(() => { preview = !preview; if (preview) previewScreen = Forms.Screen.FromPoint(Forms.Cursor.Position); Activity.Write(preview ? "Preview enabled — bottom-left corner." : "Preview disabled — following live Arena logs."); }));
        tray.ContextMenuStrip.Items.Add("Hide / show", null, (_, _) => Dispatcher.Invoke(() => { hidden = !hidden; Activity.Write(hidden ? "Overlay manually hidden." : "Overlay enabled."); }));
        tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Shutdown));
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(OpenSettings);
        overlay.Show(); overlay.Hide();
        timer.Tick += async (_, _) => await TickAsync(); timer.Start();
        arenaTimer.Tick += (_, _) => CheckArenaProcess();
        CheckArenaProcess(); arenaTimer.Start();
        Activity.Write(probe ? "Fullscreen probe enabled." : $"Log monitoring enabled. {cards.Count:N0} card definitions cached.");
        if (!File.Exists(Path.Combine(Store.Root, "settings.json")) && !probe) OpenSettings();
    }
    private void CheckArenaProcess()
    {
        try
        {
            var processes = Process.GetProcessesByName("MTGA");
            int[] ids;
            try { ids = processes.Select(process => process.Id).ToArray(); }
            finally { foreach (var process in processes) process.Dispose(); }
            if (processDetectionUnavailable) { processDetectionUnavailable = false; Activity.Write("Arena process detection restored."); }
            string? message = arenaPresence.Observe(ids) switch
            {
                ArenaPresenceChange.NotRunning => "MTG Arena is not running.",
                ArenaPresenceChange.AlreadyRunning => "MTG Arena detected — already running.",
                ArenaPresenceChange.Started => "MTG Arena started.",
                ArenaPresenceChange.Closed => "MTG Arena closed.",
                ArenaPresenceChange.Restarted => "MTG Arena restarted.",
                _ => null
            };
            if (message is not null) Activity.Write(message);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (!processDetectionUnavailable) Activity.Write("Arena process detection temporarily unavailable; will retry.");
            processDetectionUnavailable = true;
        }
    }
    internal void OpenSettings() { if (settingsWindow is null) { settingsWindow = new(this); settingsWindow.Closed += (_, _) => settingsWindow = null; settingsWindow.Show(); } else settingsWindow.Activate(); }
    internal async Task SaveSettingsAsync(Settings settings)
    {
        settings.Validate(); await Store.WriteAsync("settings.json", settings);
        
        Settings = settings; attempted.Clear(); Status = null; Activity.Write("Settings saved. Card lookups can retry.");
    }
    internal async Task ImportMetadataAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        var imported = await Task.Run(() => Metadata.ImportAsync(file, ct), ct);
        await MergeCardsAsync(imported, ct);
        Activity.Write($"Imported {imported.Count:N0} card definitions.");
    }
    private async Task MergeCardsAsync(Dictionary<int, Card> incoming, CancellationToken ct)
    {
        await cardGate.WaitAsync(ct);
        try
        {
            var merged = new Dictionary<int, Card>(cards);
            foreach (var pair in incoming) merged[pair.Key] = pair.Value;
            await Store.WriteAsync("cards.json", merged, ct); cards = merged;
        }
        finally { cardGate.Release(); }
    }
    private async Task TickAsync()
    {
        if (busy) return; busy = true;
        try
        {
            if (!probe && !preview)
                try
                {
                    if (tailPath != Settings.LogPath) { reader.Reset(); tailer = new(reader); tailPath = Settings.LogPath; }
                    var activeTailer = tailer; string path = tailPath;
                    await Task.Run(() => activeTailer.PollAsync(path, shutdown.Token), shutdown.Token);
                    if (logUnavailable) { logUnavailable = false; Status = null; Activity.Write("Arena log available again."); }
                }
                catch (IOException) { logUnavailable = true; Status = "Arena log unavailable. Enable detailed logs or check the path."; overlay.Hide(); return; }
            var pack = probe || preview ? new DraftPack("preview", "DEMO", "PremierDraft", 1, 2, [], true) : reader.Current;
            pack = pack with { Set = string.IsNullOrWhiteSpace(Settings.SetOverride) ? pack.Set : Settings.SetOverride.ToUpperInvariant(), Format = string.IsNullOrWhiteSpace(Settings.FormatOverride) ? pack.Format : Settings.FormatOverride };
            Dataset? stats = Store.ResolveDataset(pack.Set, pack.Format);
            string phase = probe ? "fullscreen probe active" : preview ? "preview active" : !pack.Active ? "waiting for a draft" : pack.Cards.Length == 0 ? "waiting for Arena pack data" : $"tracking pack {pack.Pack}, pick {pack.Pick}";
            string draftState = $"{pack.Active}:{pack.DraftId}:{pack.Pack}:{pack.Pick}:{string.Join(',', pack.Cards)}:{stats?.Identity}:{stats?.Format}:{preview}:{probe}";
            if (draftState != lastDraft)
            {
                lastDraft = draftState;
                Activity.Write(preview || probe ? "Preview active — displaying sample cards." : !pack.Active ? "Waiting for a draft." : pack.Cards.Length == 0 && !preview && !probe
                    ? $"{pack.Set ?? "Unknown set"} / {pack.Format ?? "Unknown format"}: waiting for Arena pack data."
                    : $"{phase}: {pack.Cards.Length} cards; " + (stats is null ? "no matching statistics imported." : stats.Format != pack.Format ? "using Premier Draft statistics as a Quick Draft fallback." : "matching statistics loaded."));
            }
            var rows = Ranking.Rank(pack, cards, stats);
            if (probe || preview) rows = [new(1, "Fullscreen visibility test", "{2}{W}{U}", .587, 100), new(2, "Split card example", "{R} // {3}{G}", null, 0)];
            overlay.Render(pack, rows, stats, Settings, Status, probe);
            if (!hidden && pack.Active)
            {
                overlay.Show(); overlay.UpdateLayout();
                if (!Native.Position(overlay, Settings.OffsetX, Settings.OffsetY))
                { if (preview && !probe) Native.PositionPreview(overlay, Settings.OffsetX, Settings.OffsetY, previewScreen); else overlay.Hide(); }
            }
            else overlay.Hide();
            var missing = (fetching ? Array.Empty<int>() : pack.Cards).Where(id => (!cards.TryGetValue(id, out var card) || card.ManaCost == "?")
                && (!attempted.TryGetValue(id, out var retryAfter) || DateTimeOffset.UtcNow >= retryAfter)).ToArray();
            foreach (int id in missing) attempted[id] = DateTimeOffset.UtcNow.AddMinutes(1);
            // Network work never blocks following the log or repositioning the overlay.
            if (missing.Length > 0) _ = RefreshMetadataAsync(missing);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { Status = "Data unavailable: " + ex.Message; overlay.Hide(); }
        finally { busy = false; }
    }
    private async Task RefreshMetadataAsync(int[] ids)
    {
        fetching = true; Activity.Write($"Looking up metadata for {ids.Length} card(s)…");
        try
        {
            var progress = new Progress<string>(Activity.Write);
            var fetched = await metadataClient.FetchAsync(ids, progress, shutdown.Token);
            await MergeCardsAsync(fetched, shutdown.Token);
            int unresolved = ids.Count(id => !cards.ContainsKey(id));
            bool missingCosts = ids.Any(id => cards.TryGetValue(id, out var card) && card.ManaCost == "?");
            Status = unresolved > 0 ? $"{unresolved} card(s) still unknown. Retrying automatically in one minute."
                : missingCosts ? "Some mana costs unavailable; names and win rates are loaded. Retrying automatically." : null;
            Activity.Write($"Card lookup completed: {fetched.Count} definitions cached; {unresolved} requested card(s) unresolved.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { Status = "Card lookup unavailable. Retrying automatically in one minute."; Activity.Write($"Card lookup error: {ex.GetType().Name}" + (ex is System.Net.Http.HttpRequestException { StatusCode: { } code } ? $" (HTTP {(int)code})" : "")); }
        finally { foreach (int id in ids) attempted[id] = DateTimeOffset.UtcNow.AddMinutes(1); fetching = false; }
    }
    protected override void OnExit(ExitEventArgs e) { arenaTimer.Stop(); timer.Stop(); shutdown.Cancel(); tray.Visible = false; tray.Dispose(); trayIcon?.Dispose(); metadataHttp.Dispose(); overlay.Close(); base.OnExit(e); }
}
