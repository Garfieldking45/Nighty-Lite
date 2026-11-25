using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Nighty;

/// <summary>Click-only number box: [-] value [+]. The overlay never takes keyboard focus, so nothing can be typed into it.</summary>
public partial class Stepper : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Stepper),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((Stepper)d).UpdateText()));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Min { get; set; } = 0;
    public double Max { get; set; } = 100;
    public double Step { get; set; } = 1;
    public string Format { get; set; } = "0.##";
    /// <summary>For settings where 0 means "off/auto" and anything else has a minimum: values between 0 and Floor snap to one or the other.</summary>
    public double Floor { get; set; } = double.NaN;
    public string? ZeroText { get; set; }

    public Stepper()
    {
        InitializeComponent();
        UpdateText();
    }

    private void UpdateText()
    {
        if (Display == null) return;
        Display.Text = Value == 0 && ZeroText != null ? ZeroText : Value.ToString(Format, CultureInfo.CurrentCulture);
    }

    private void Nudge(int dir)
    {
        double v = Math.Round(Value + dir * Step, 3);
        if (!double.IsNaN(Floor) && v > 0 && v < Floor) v = dir > 0 ? Floor : 0;
        Value = Math.Clamp(v, Min, Max);
        UpdateText();
    }

    private void Minus_Click(object sender, RoutedEventArgs e) => Nudge(-1);
    private void Plus_Click(object sender, RoutedEventArgs e) => Nudge(1);
}

/// <summary>Lets a Stepper (double) edit an int setting.</summary>
public sealed class IntConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => System.Convert.ToDouble(value);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => (int)Math.Round((double)value);
}
