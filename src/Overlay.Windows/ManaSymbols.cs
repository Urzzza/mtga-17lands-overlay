using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Overlay.Core;

namespace Overlay.Windows;
internal static class ManaSymbols
{
    private static readonly FontFamily Font = new(new Uri("pack://application:,,,/Overlay.Windows;component/Assets/Mana/"), "./#Mana");
    internal static StackPanel Render(string cost)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 4, 0) };
        if (cost is "" or "?") { panel.Children.Add(Label(cost == "" ? "—" : "?", 12)); return panel; }
        foreach (var token in ManaCost.Parse(cost))
        {
            if (token.Separator) { panel.Children.Add(Label(" // ", 11)); continue; }
            var grid = new Grid { Width = 18, Height = 18, Margin = new Thickness(1, 0, 1, 0),
                Clip = new EllipseGeometry(new Point(9, 9), 9, 9), ToolTip = "{" + token.Text + "}" };
            if (token.Hybrid)
            {
                var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                brush.GradientStops.Add(new GradientStop(ColorFor(token.First), 0));
                brush.GradientStops.Add(new GradientStop(ColorFor(token.First), .5));
                brush.GradientStops.Add(new GradientStop(ColorFor(token.Second!), .5));
                brush.GradientStops.Add(new GradientStop(ColorFor(token.Second!), 1));
                grid.Background = brush;
                var canvas = new Canvas { Width = 18, Height = 18 };
                var first = Glyph(token.Phyrexian ? "P" : token.First, 8.5);
                var second = Glyph(token.Phyrexian ? "P" : token.Second!, 8.5);
                Canvas.SetLeft(first, 1); Canvas.SetTop(first, 1);
                Canvas.SetLeft(second, 8.5); Canvas.SetTop(second, 8.5);
                canvas.Children.Add(first); canvas.Children.Add(second); grid.Children.Add(canvas);
            }
            else
            {
                grid.Background = new SolidColorBrush(ColorFor(token.First));
                grid.Children.Add(Glyph(token.Phyrexian ? "P" : token.First, 13));
            }
            panel.Children.Add(grid);
        }
        return panel;
    }
    private static TextBlock Label(string text, double size) => new() { Text = text, FontSize = size, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private static Color ColorFor(string symbol) => (Color)ColorConverter.ConvertFromString(symbol switch
    { "W" => "#FDFBCE", "U" => "#BCDAF7", "B" => "#A7999E", "R" => "#F19B79", "G" => "#9FCBA6", _ => "#D0C6BB" });
    private static Viewbox Glyph(string symbol, double size)
    {
        string? glyph = symbol switch
        {
            "W" => "\ue600", "U" => "\ue601", "B" => "\ue602", "R" => "\ue603", "G" => "\ue604",
            "C" => "\ue904", "P" => "\ue618", "S" => "\ue619", "X" => "\ue615", "Y" => "\ue616", "Z" => "\ue617", _ => null
        };
        if (glyph is null && int.TryParse(symbol, NumberStyles.None, CultureInfo.InvariantCulture, out int generic) && generic is >= 0 and <= 20)
            glyph = char.ConvertFromUtf32(generic <= 15 ? 0xe605 + generic : 0xe62a + generic - 16);
        var text = new TextBlock { Text = glyph ?? symbol, FontFamily = glyph is not null ? Font : new FontFamily("Segoe UI"),
            FontSize = 16, Foreground = Brushes.Black, TextAlignment = TextAlignment.Center };
        return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = text,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }
}
