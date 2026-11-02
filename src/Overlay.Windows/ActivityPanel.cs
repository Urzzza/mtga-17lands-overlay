using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Overlay.Core;

namespace Overlay.Windows;
internal sealed class ActivityPanel : DockPanel
{
    private readonly ActivityLog log;
    private readonly TextBox box = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12,
        Background = new SolidColorBrush(Color.FromRgb(18, 24, 33)), Foreground = Brushes.Gainsboro, Padding = new Thickness(10) };
    private readonly TextBlock alive = new() { Margin = new Thickness(0, 4, 0, 8), Foreground = Brushes.DarkGreen };
    private readonly CheckBox scroll = new() { Content = "Auto-scroll", IsChecked = true, Margin = new Thickness(12, 4, 0, 0) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long version = -1;
    private ActivityPanel(ActivityLog log)
    {
        this.log = log; Margin = new Thickness(12, 20, 20, 20);
        var title = new StackPanel(); title.Children.Add(new TextBlock { Text = "Live activity", FontSize = 18, FontWeight = FontWeights.SemiBold }); title.Children.Add(alive);
        SetDock(title, Dock.Top); Children.Add(title);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var clear = new Button { Content = "Clear log", Padding = new Thickness(8, 3, 8, 3) }; clear.Click += (_, _) => log.Clear();
        controls.Children.Add(clear); controls.Children.Add(scroll); SetDock(controls, Dock.Bottom); Children.Add(controls);
        Children.Add(box);
        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); timer.Start(); }; Unloaded += (_, _) => timer.Stop();
    }
    private void Refresh()
    {
        var snapshot = log.Snapshot();
        alive.Text = $"App running • {DateTime.Now:HH:mm:ss}";
        if (snapshot.Version == version) return;
        version = snapshot.Version;
        double offset = box.VerticalOffset;
        int selectionStart = box.SelectionStart, selectionLength = box.SelectionLength;
        box.Text = string.Join(Environment.NewLine, snapshot.Entries.Select(e => $"{e.Time.ToLocalTime():HH:mm:ss}  {e.Message}"));
        if (scroll.IsChecked == true) box.ScrollToEnd();
        else
        {
            int start = Math.Min(selectionStart, box.Text.Length);
            box.Select(start, Math.Min(selectionLength, box.Text.Length - start));
            box.ScrollToVerticalOffset(offset);
        }
    }
    internal static Grid Beside(UIElement content, ActivityLog log)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 340 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 300 });
        grid.Children.Add(content);
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.LightGray };
        Grid.SetColumn(splitter, 1); grid.Children.Add(splitter);
        var activity = new ActivityPanel(log); Grid.SetColumn(activity, 2); grid.Children.Add(activity); return grid;
    }
}
