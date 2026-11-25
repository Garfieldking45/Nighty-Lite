using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nighty.Services;

namespace Nighty;

/// <summary>
/// A small normal window for typing a suggestion. The overlay never takes keyboard focus (so Roblox keeps it),
/// which is why this is a separate window. Nothing is sent until the user presses Send.
/// </summary>
public sealed class SuggestionWindow : Window
{
    private const int MaxChars = 500;
    private readonly TextBox _box = new();
    private readonly TextBlock _count = new() { Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _send = new() { Content = "Send", Padding = new Thickness(18, 6, 18, 6), IsDefault = false };

    public SuggestionWindow()
    {
        Title = "Nighty Lite - Send a suggestion";
        Width = 440; Height = 330; MinWidth = 360; MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;

        _box.AcceptsReturn = true;
        _box.TextWrapping = TextWrapping.Wrap;
        _box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _box.MaxLength = MaxChars;
        _box.Padding = new Thickness(6);
        _box.TextChanged += (_, _) => { _count.Text = $"{_box.Text.Length}/{MaxChars}"; _send.IsEnabled = _box.Text.Trim().Length >= 3; };
        _send.Click += async (_, _) => await SendAsync();
        _send.IsEnabled = false;
        _count.Text = $"0/{MaxChars}";

        var note = new TextBlock
        {
            Text = "Tell the author what to add or fix. It is sent with your app version and a random install ID only. " +
                   "Please don't include personal info.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Foreground = Brushes.Gray,
        };

        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_send);

        var grid = new Grid { Margin = new Thickness(14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(grid, note, 0); Place(grid, _box, 1); Place(grid, _count, 2); Place(grid, _status, 3); Place(grid, buttons, 4);
        Content = grid;

        Loaded += (_, _) => { Activate(); _box.Focus(); };
    }

    private static void Place(Grid g, UIElement e, int row) { Grid.SetRow(e, row); g.Children.Add(e); }

    private async Task SendAsync()
    {
        _send.IsEnabled = false;
        _box.IsEnabled = false;
        _status.Foreground = Brushes.Gray;
        _status.Text = "Sending...";
        var (ok, message) = await StatsService.SendSuggestionAsync(Svc.S, _box.Text);
        _status.Foreground = ok ? Brushes.SeaGreen : Brushes.IndianRed;
        _status.Text = message;
        if (ok) { await Task.Delay(1500); Close(); return; }
        _box.IsEnabled = true;
        _send.IsEnabled = true;
    }
}
