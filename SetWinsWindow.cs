using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Nighty.Models;

namespace Nighty;

/// <summary>A small normal window for typing your win count (the overlay never takes keyboard focus, so it cannot hold a text box).</summary>
public sealed class SetWinsWindow : Window
{
    private readonly WinsConfig _wins;
    private readonly TextBox _box = new() { Padding = new Thickness(6), FontSize = 18, MaxLength = 6, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.IndianRed, Margin = new Thickness(0, 6, 0, 0), Height = 18 };

    public SetWinsWindow(WinsConfig wins)
    {
        _wins = wins;
        Title = "Nighty Lite - Set wins";
        Width = 300; Height = 190; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        _box.Text = wins.Count.ToString();
        _box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);   // digits only
        DataObject.AddPastingHandler(_box, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string s || !s.All(char.IsAsciiDigit)) e.CancelCommand();
        });

        var ok = new Button { Content = "Set", Padding = new Thickness(18, 6, 18, 6), IsDefault = true };
        ok.Click += (_, _) => Apply();
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = $"How many wins do you have? (0 - {WinsConfig.MaxWins:N0})", Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_box);
        panel.Children.Add(_error);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => { Activate(); _box.Focus(); _box.SelectAll(); };
    }

    private void Apply()
    {
        if (!int.TryParse(_box.Text, out int n) || n < 0 || n > WinsConfig.MaxWins) { _error.Text = $"Enter a number from 0 to {WinsConfig.MaxWins:N0}."; return; }
        _wins.Count = n;
        Close();
    }
}
