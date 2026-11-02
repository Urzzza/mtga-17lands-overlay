using System;
using System.Collections.Generic;
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
    private Dictionary<int, Card> cards = new();
    private readonly HashSet<int> attempted = new();
    private readonly OverlayWindow overlay = new();
    private readonly DraftReader reader = new();
    private LogTailer tailer = null!;
    private Forms.NotifyIcon tray = null!;
    private readonly CancellationTokenSource shutdown = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool busy, hidden, preview;
    private string tailPath = "";
    private bool fetching;
    private readonly SemaphoreSlim cardGate = new(1, 1);
    private readonly bool probe = Environment.GetCommandLineArgs().Contains("--fullscreen-probe");
    internal string? Status;
    private SettingsWindow? settingsWindow;
    [STAThread] public static void Main() { new Program().Run(); }
    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try { Settings = Store.Read<Settings>("settings.json") ?? new(); Settings.Validate(); cards = Store.Read<Dictionary<int, Card>>("cards.json") ?? new(); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or ArgumentException) { Status = "Settings/cache could not be loaded. Open settings to repair."; Settings = new(); }
        tailer = new(reader);
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Text = "MTGA 17Lands Overlay", Visible = true, ContextMenuStrip = new Forms.ContextMenuStrip() };
        tray.ContextMenuStrip.Items.Add("Settings / import data", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        tray.ContextMenuStrip.Items.Add("Preview", null, (_, _) => Dispatcher.Invoke(() => preview = !preview));
        tray.ContextMenuStrip.Items.Add("Hide / show", null, (_, _) => Dispatcher.Invoke(() => hidden = !hidden));
        tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Shutdown));
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(OpenSettings);
        overlay.Show(); overlay.Hide();
        timer.Tick += async (_, _) => await TickAsync(); timer.Start();
        if (!File.Exists(Path.Combine(Store.Root, "settings.json")) && !probe) OpenSettings();
    }
    internal void OpenSettings() { if (settingsWindow is null) { settingsWindow = new(this); settingsWindow.Closed += (_, _) => settingsWindow = null; settingsWindow.Show(); } else settingsWindow.Activate(); }
    internal async Task SaveSettingsAsync(Settings settings)
    {
        settings.Validate(); await Store.WriteAsync("settings.json", settings);
        
        Settings = settings; attempted.Clear(); Status = null;
    }
    internal async Task ImportMetadataAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        var imported = await Task.Run(() => Metadata.ImportAsync(file, ct), ct);
        await MergeCardsAsync(imported, ct);
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
                }
                catch (IOException) { Status = "Arena log unavailable. Enable detailed logs or check the path."; overlay.Hide(); return; }
            var pack = probe || preview ? new DraftPack("preview", "DEMO", "PremierDraft", 1, 2, [], true) : reader.Current;
            pack = pack with { Set = string.IsNullOrWhiteSpace(Settings.SetOverride) ? pack.Set : Settings.SetOverride.ToUpperInvariant(), Format = string.IsNullOrWhiteSpace(Settings.FormatOverride) ? pack.Format : Settings.FormatOverride };
            Dataset? stats = Store.LoadDataset(pack.Set, pack.Format);
            var rows = Ranking.Rank(pack, cards, stats);
            if (probe || preview) rows = [new(1, "Fullscreen visibility test", "{2}{W}{U}", .587, 100), new(2, "Split card example", "{R} // {3}{G}", null, 0)];
            overlay.Render(pack, rows, stats, Settings, Status, probe);
            if (!hidden && pack.Active)
            {
                overlay.Show(); overlay.UpdateLayout();
                if (!Native.Position(overlay, Settings.OffsetX, Settings.OffsetY))
                { if (preview && !probe) { overlay.Left = 40; overlay.Top = 80; } else overlay.Hide(); }
            }
            else overlay.Hide();
            var missing = (fetching ? Array.Empty<int>() : pack.Cards).Where(id => !cards.ContainsKey(id) && attempted.Add(id)).ToArray();
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
        fetching = true;
        try
        {
            var fetched = await Metadata.FetchAsync(ids, shutdown.Token);
            await MergeCardsAsync(fetched, shutdown.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { Status = "Card lookup unavailable. Import Scryfall JSON in settings; save settings to retry."; }
        finally { fetching = false; }
    }
    protected override void OnExit(ExitEventArgs e) { timer.Stop(); shutdown.Cancel(); tray.Visible = false; tray.Dispose(); overlay.Close(); base.OnExit(e); }
}
