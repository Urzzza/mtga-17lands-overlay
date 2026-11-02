using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Overlay.Core;

namespace Overlay.Windows;
internal sealed class DownloadWindow : Window
{
    private readonly Program app;
    private readonly ListBox sets = new() { SelectionMode = SelectionMode.Multiple, Height = 200 };
    private readonly StackPanel formats = new();
    private readonly TextBlock availability = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBox results = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button refresh = new() { Content = "Refresh available datasets" };
    private readonly Button download = new() { Content = "Download and import selected", IsEnabled = false, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Dictionary<string, CheckBox> formatChoices = new();
    private PublicDataset[] catalog = [];
    private readonly Dictionary<string, string> setNames = SetNames.Bundled();
    private DownloadSelection selection = new([], ["PremierDraft"]);
    private CancellationTokenSource? operation;
    private string currentKey = "";
    internal DownloadWindow(Program app)
    {
        this.app = app; Title = "Download 17Lands datasets"; Width = 1100; MinWidth = 800; Height = 750; Icon = AppIcon.Image;
        var panel = new StackPanel { Margin = new Thickness(20) };
        Content = ActivityPanel.Beside(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, app.Activity);
        void Label(string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        Label("Choose sets and draft formats. The app downloads published 17Lands public game datasets and calculates GIH rates locally. Files can be large and importing can take several minutes. No token is needed.");
        sets.DisplayMemberPath = nameof(SetOption.Display);
        panel.Children.Add(refresh); Label("Sets — click to select one or more:"); panel.Children.Add(sets);
        Label("Formats:"); panel.Children.Add(formats);
        try { selection = app.Store.Read<DownloadSelection>("downloads.json") ?? selection; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { status.Text = "Previous selections could not be loaded."; }
        try
        {
            var cached = app.Store.Read<Dictionary<string, string>>("set-names.json");
            if (cached is not null) foreach (var pair in cached) if (!string.IsNullOrWhiteSpace(pair.Value)) setNames[pair.Key] = pair.Value;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { /* Bundled names remain available. */ }
        foreach (var f in Storage.Formats)
        {
            var box = new CheckBox { Content = f, IsChecked = selection.Formats?.Contains(f) == true, Margin = new Thickness(0, 3, 0, 3) };
            formatChoices[f] = box; formats.Children.Add(box);
            box.Checked += (_, _) => UpdateSelection(); box.Unchecked += (_, _) => UpdateSelection();
        }
        panel.Children.Add(availability); panel.Children.Add(download);
        var cancel = new Button { Content = "Cancel current downloads / import", Margin = new Thickness(0, 8, 0, 0) };
        cancel.Click += (_, _) => operation?.Cancel(); panel.Children.Add(cancel);
        panel.Children.Add(status); panel.Children.Add(results);
        Label("Only published set/format pairs are downloaded. Missing pairs are skipped, with no substitution. Completed imports replace their existing statistics; failed or cancelled imports preserve the previous data. Temporary source downloads are deleted afterward. Data: 17Lands.com/public_datasets (CC BY 4.0).");
        sets.SelectionChanged += (_, _) => UpdateSelection();
        refresh.Click += async (_, _) => await RefreshAsync();
        download.Click += async (_, _) => await DownloadAsync();
        Loaded += async (_, _) => await RefreshAsync();
        Closing += (_, e) => { if (operation is not null) { operation.Cancel(); e.Cancel = true; status.Text = "Cancelling… Close again when finished."; } };
    }
    private PublicDataset[] Selected() => catalog.Where(e => sets.SelectedItems.Cast<SetOption>().Select(s => s.Code).Contains(e.Set) && formatChoices[e.Format].IsChecked == true).ToArray();
    private void UpdateSelection()
    {
        int pairs = Selected().Length;
        int requested = sets.SelectedItems.Count * formatChoices.Values.Count(c => c.IsChecked == true);
        availability.Text = $"{pairs} published dataset(s) selected. {requested - pairs} unavailable set/format pair(s) will be skipped.";
        download.IsEnabled = operation is null && pairs > 0;
    }
    private void Busy(bool busy)
    {
        refresh.IsEnabled = sets.IsEnabled = formats.IsEnabled = !busy;
        UpdateSelection();
    }
    private static bool Expected(Exception ex) => ex is IOException or HttpRequestException or JsonException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or KeyNotFoundException or Microsoft.VisualBasic.FileIO.MalformedLineException;
    private async Task RefreshAsync()
    {
        if (operation is not null) return;
        if (catalog.Length > 0) selection = new(sets.SelectedItems.Cast<SetOption>().Select(s => s.Code).ToArray(), formatChoices.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToArray());
        operation = new(); Busy(true); status.Text = "Loading published datasets…"; app.Activity.Write(status.Text);
        try
        {
            using var http = PublicDataClient.CreateHttpClient();
            catalog = await new PublicDataClient(http).GetCatalogAsync(operation.Token);
            string nameNotice = "";
            try
            {
                if (catalog.Any(entry => !setNames.ContainsKey(entry.Set)))
                {
                    var latest = await SetNames.FetchAsync(http, operation.Token);
                    foreach (var pair in latest) setNames[pair.Key] = pair.Value;
                    await app.Store.WriteAsync("set-names.json", setNames, operation.Token);
                }
            }
            catch (OperationCanceledException) when (!operation.IsCancellationRequested) { nameNotice = " Using bundled/cached set names."; }
            catch (Exception ex) when (Expected(ex)) { nameNotice = " Using bundled/cached set names."; }
            sets.Items.Clear();
            foreach (string set in catalog.Select(e => e.Set).Distinct())
            {
                var item = new SetOption(set, SetNames.Name(set, setNames)); sets.Items.Add(item);
                if (selection.Sets?.Contains(set) == true) sets.SelectedItems.Add(item);
            }
            status.Text = $"Found {catalog.Length} published datasets. New sets appear when 17Lands releases public game data." + nameNotice;
        }
        catch (OperationCanceledException) { status.Text = operation.IsCancellationRequested ? "Catalog loading cancelled." : "Catalog request timed out. Try refreshing again."; }
        catch (Exception ex) when (Expected(ex)) { status.Text = "Could not load the public catalog: " + ex.Message + " Manual import remains available in settings."; }
        finally { app.Activity.Write(status.Text); operation.Dispose(); operation = null; Busy(false); }
    }
    private async Task DownloadAsync()
    {
        if (operation is not null) return;
        var queue = Selected(); if (queue.Length == 0) return;
        operation = new(); Busy(true); results.Clear(); int completed = 0, failed = 0;
        app.Activity.Write($"Starting download queue: {queue.Length} dataset(s).");
        try
        {
            await app.Store.WriteAsync("downloads.json", new DownloadSelection(sets.SelectedItems.Cast<SetOption>().Select(s => s.Code).ToArray(), formatChoices.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToArray()), operation.Token);
            using var http = PublicDataClient.CreateHttpClient(); var client = new PublicDataClient(http);
            foreach (var entry in queue)
            {
                operation.Token.ThrowIfCancellationRequested();
                currentKey = $"{new SetOption(entry.Set, SetNames.Name(entry.Set, setNames)).Display} / {entry.Format}";
                string key = currentKey;
                status.Text = $"{completed + failed + 1}/{queue.Length} — {key}: downloading…";
                app.Activity.Write(status.Text);
                var progress = new Progress<DownloadProgress>(p =>
                {
                    if (operation is null || currentKey != key) return;
                    status.Text = p.Stage == "Downloading"
                        ? $"{key}: downloaded {p.Bytes / 1048576.0:N1} MiB" + (p.TotalBytes.HasValue ? $" / {p.TotalBytes.Value / 1048576.0:N1} MiB" : "")
                        : p.Stage == "Importing" ? $"{key}: importing {p.Games:N0} games…" : $"{key}: complete.";
                    app.Activity.Progress("download-" + p.Stage, status.Text);
                });
                try
                {
                    await client.DownloadAndImportAsync(entry, app.Store, progress, operation.Token);
                    completed++; results.AppendText($"Imported {key} (published update {entry.Updated}).\n"); app.Activity.Write($"Imported and saved {key}.");
                }
                catch (OperationCanceledException) when (!operation.IsCancellationRequested)
                { failed++; results.AppendText($"Failed {key}: download timed out; existing data retained.\n"); app.Activity.Write($"Download timed out: {key}."); }
                catch (Exception ex) when (Expected(ex))
                { failed++; results.AppendText($"Failed {key}: {ex.Message}\n"); app.Activity.Write($"Failed {key}: {ex.Message}"); }
            }
            status.Text = $"Finished: {completed} imported, {failed} failed. Previous data was retained for failed imports.";
        }
        catch (OperationCanceledException) { status.Text = $"Cancelled: {completed} imported, {failed} failed. Remaining datasets were not downloaded."; }
        catch (Exception ex) when (Expected(ex)) { status.Text = ex.Message; }
        finally { app.Activity.Write(status.Text); currentKey = ""; operation.Dispose(); operation = null; Busy(false); }
    }
}
