using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Overlay.Core;
using Microsoft.Win32;

namespace Overlay.Windows;
internal sealed class SettingsWindow : Window
{
    private readonly Program app;
    private readonly StackPanel panel = new() { Margin = new Thickness(20) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBox log, set, scale, opacity, x, y, importSet;
    private readonly ComboBox format, importFormat;
    private CancellationTokenSource? import;
    private readonly Button gameImport = new() { Content = "Import 17Lands game CSV / CSV.GZ" };
    private readonly Button metadataImport = new() { Content = "Import Scryfall metadata JSON" };
    internal SettingsWindow(Program app)
    {
        this.app = app; Title = "MTGA overlay — settings and data"; Width = 1060; MinWidth = 800; Height = 740; Icon = AppIcon.Image;
        Content = ActivityPanel.Beside(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, app.Activity);
        Label("Arena: Options → Account → Detailed Logs (Plugin Support), then restart Arena. Logs remain on this PC.");
        log = Field("Arena Player.log path", app.Settings.LogPath);
        var browse = Button("Choose log", () => { var dialog = new OpenFileDialog { Filter = "Arena log|*.log|All files|*.*" }; if (dialog.ShowDialog() == true) log.Text = dialog.FileName; });
        set = Field("Set override (blank = automatic)", app.Settings.SetOverride ?? "");
        format = Format("Format override", true, app.Settings.FormatOverride);
        scale = Field("Scale (0.5–2)", app.Settings.Scale.ToString(CultureInfo.InvariantCulture));
        opacity = Field("Opacity (0.2–1)", app.Settings.Opacity.ToString(CultureInfo.InvariantCulture));
        x = Field("Left offset (logical pixels)", app.Settings.OffsetX.ToString(CultureInfo.InvariantCulture));
        y = Field("Bottom offset (logical pixels)", app.Settings.OffsetY.ToString(CultureInfo.InvariantCulture));
        var save = Button("Save settings / retry card lookup", async () => await RunAsync(async () =>
        {
            double Read(TextBox field) => double.Parse(field.Text, CultureInfo.InvariantCulture);
            await app.SaveSettingsAsync(new Settings { LogPath = log.Text.Trim(), SetOverride = set.Text.Trim(), FormatOverride = format.SelectedItem?.ToString(), Scale = Read(scale), Opacity = Read(opacity), OffsetX = Read(x), OffsetY = Read(y) });
            status.Text = "Settings saved.";
        }));
        Label("17Lands public data — https://www.17lands.com/public_datasets\nSelect sets and formats to download automatically, or import a previously downloaded file below. No API token is needed. Imports can take several minutes. Locally calculated rates can differ from the website’s filtered statistics.");
        Button("Select sets and formats to download…", () => new DownloadWindow(app) { Owner = this }.ShowDialog());
        importSet = Field("Dataset set code (for example DSK)", app.Settings.SetOverride ?? "");
        importFormat = Format("Dataset format", false, "PremierDraft");
        gameImport.Margin = new Thickness(0, 8, 0, 0); panel.Children.Add(gameImport);
        gameImport.Click += async (_, _) => await ImportAsync(false);
        panel.Children.Add(metadataImport); metadataImport.Click += async (_, _) => await ImportAsync(true);
        Button("Cancel import", () => import?.Cancel());
        Label("Metadata: import a Scryfall bulk card JSON array for offline use. Missing pack cards are otherwise looked up at api.scryfall.com and cached. No Arena logs are sent.");
        panel.Children.Add(status); status.Text = app.Status ?? "Ready. The tray menu provides preview and hide/show.";
        Closing += (_, e) => { if (import is not null) { import.Cancel(); e.Cancel = true; status.Text = "Cancelling import; close again once complete."; } };
    }
    private void Label(string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) });
    private TextBox Field(string label, string value) { Label(label); var field = new TextBox { Text = value }; panel.Children.Add(field); return field; }
    private ComboBox Format(string label, bool automatic, string? value)
    {
        Label(label); var field = new ComboBox(); if (automatic) field.Items.Add(""); foreach (var f in Storage.Formats) field.Items.Add(f);
        field.SelectedItem = value ?? (automatic ? "" : "PremierDraft"); panel.Children.Add(field); return field;
    }
    private Button Button(string label, Action action)
    { var button = new Button { Content = label, Margin = new Thickness(0, 8, 0, 0) }; button.Click += (_, _) => action(); panel.Children.Add(button); return button; }
    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { status.Text = "Import cancelled. Existing data retained."; app.Activity.Write(status.Text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or System.Text.Json.JsonException or Microsoft.VisualBasic.FileIO.MalformedLineException)
        { status.Text = ex.Message; app.Activity.Write("Import/settings error: " + ex.Message); }
    }
    private async Task ImportAsync(bool metadata)
    {
        if (import is not null) return;
        var dialog = new OpenFileDialog { Filter = metadata ? "Scryfall JSON|*.json" : "17Lands game data|*.csv;*.csv.gz" };
        if (dialog.ShowDialog() != true) return;
        string chosenSet = importSet.Text.Trim().ToUpperInvariant(), chosenFormat = importFormat.SelectedItem?.ToString() ?? "PremierDraft";
        import = new CancellationTokenSource(); var ct = import.Token;
        gameImport.IsEnabled = metadataImport.IsEnabled = false;
        await RunAsync(async () =>
        {
            status.Text = "Importing…";
            app.Activity.Write(metadata ? "Importing local card metadata…" : $"Importing local game data: {chosenSet} / {chosenFormat}…");
            if (metadata) await app.ImportMetadataAsync(dialog.FileName, ct);
            else
            {
                var progress = new Progress<long>(rows => { status.Text = $"Processed {rows:N0} games…"; app.Activity.Progress("local-import", status.Text); });
                var data = await StatisticsImporter.ImportAsync(dialog.FileName, chosenSet, chosenFormat, progress, ct);
                await app.Store.WriteAsync(Path.GetFileName(app.Store.DatasetPath(chosenSet, chosenFormat)), data, ct);
                status.Text = $"Imported {data.Set} / {data.Format}. Coverage: {data.CoverageStart ?? "unknown"} – {data.CoverageEnd ?? "unknown"}.";
            }
            if (metadata) status.Text = "Metadata imported.";
            app.Activity.Write(status.Text);
        });
        import.Dispose(); import = null; gameImport.IsEnabled = metadataImport.IsEnabled = true;
    }
}
