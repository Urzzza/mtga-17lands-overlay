using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Overlay.Core;

namespace Overlay.Windows;
internal sealed class OverlayWindow : Window
{
    private readonly StackPanel panel = new();
    internal OverlayWindow()
    {
        Title = "MTGA 17Lands Overlay"; Icon = AppIcon.Image; Width = 430; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; IsHitTestVisible = false;
        Content = new Border { Background = new SolidColorBrush(Color.FromRgb(20, 23, 30)), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = panel };
        SourceInitialized += (_, _) => Native.MakeOverlay(this);
    }
    private static TextBlock Text(string text, double size, Brush color) => new() { Text = text, FontSize = size, Foreground = color, TextWrapping = TextWrapping.Wrap };
    internal void Render(DraftPack pack, RankedCard[] rows, Dataset? stats, Settings settings, string? status, bool probe)
    {
        panel.Children.Clear(); Opacity = settings.Opacity;
        panel.LayoutTransform = new ScaleTransform(settings.Scale, settings.Scale); Width = 430 * settings.Scale;
        panel.Children.Add(Text(probe ? "FULLSCREEN PROBE • Alt+Enter / Alt+Tab" : $"{pack.Set ?? "Unknown set"} • {pack.Format ?? "Unknown format"} • Pack {pack.Pack} Pick {pack.Pick}", 13, Brushes.LightSkyBlue));
        if (rows.Length == 0) panel.Children.Add(Text("Waiting for Arena pack data.", 15, Brushes.White));
        foreach (var row in rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            var name = Text(row.Name, 13, Brushes.White); name.TextWrapping = TextWrapping.Wrap;
            grid.Children.Add(name);
            var mana = Mana(row.ManaCost); Grid.SetColumn(mana, 1); grid.Children.Add(mana);
            var rate = Text(row.Rate.HasValue ? row.Rate.Value.ToString("P1", CultureInfo.InvariantCulture) : "N/A", 13, Brushes.LightGreen);
            rate.TextAlignment = TextAlignment.Right; Grid.SetColumn(rate, 2); grid.Children.Add(rate); panel.Children.Add(grid);
        }
        panel.Children.Add(Text("GIH win rates • Data: 17Lands.com/public_datasets", 10, Brushes.LightGray));
        panel.Children.Add(Text(stats is null ? "No matching dataset imported" : $"Coverage: {stats.CoverageStart ?? "unknown"} – {stats.CoverageEnd ?? "unknown"}", 10, Brushes.LightGray));
        if (status is not null) panel.Children.Add(Text(status, 10, Brushes.Orange));
    }
    private static StackPanel Mana(string cost)
    {
        var result = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5, 0, 4, 0) };
        if (cost is "" or "?") { result.Children.Add(Text(cost == "" ? "—" : "?", 12, Brushes.White)); return result; }
        foreach (Match token in Regex.Matches(cost, @"\{([^}]+)\}|//"))
        {
            if (token.Value == "//") { result.Children.Add(Text(" / ", 12, Brushes.White)); continue; }
            string symbol = token.Groups[1].Value;
            Brush fill = symbol switch { "W" => Brushes.LightGoldenrodYellow, "U" => Brushes.LightSkyBlue, "B" => Brushes.SlateGray, "R" => Brushes.Salmon, "G" => Brushes.LightGreen, _ => Brushes.LightGray };
            result.Children.Add(new Border { Background = fill, CornerRadius = new CornerRadius(10), Padding = new Thickness(3, 0, 3, 0), Margin = new Thickness(1, 0, 0, 0), Child = Text(symbol, 11, Brushes.Black) });
        }
        return result;
    }
}
