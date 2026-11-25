using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AgentFlow.App.Converters;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class NullToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the value's text equals the converter parameter (for "is this the open page").</summary>
public sealed class EqualsParameterConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A soft, stable colour per project name, for its avatar in the sidebar.</summary>
public sealed class NameToBrushConverter : IValueConverter
{
    private static readonly System.Windows.Media.Color[] Palette =
    [
        System.Windows.Media.Color.FromRgb(0x7C, 0x6C, 0xF2), // violet
        System.Windows.Media.Color.FromRgb(0x3E, 0xA6, 0xF2), // sky
        System.Windows.Media.Color.FromRgb(0x2F, 0xBF, 0x9F), // mint
        System.Windows.Media.Color.FromRgb(0xF2, 0x8C, 0x4E), // apricot
        System.Windows.Media.Color.FromRgb(0xE8, 0x5D, 0x8E), // berry
        System.Windows.Media.Color.FromRgb(0xC9, 0xA2, 0x27), // honey
        System.Windows.Media.Color.FromRgb(0x5B, 0x8D, 0xEF), // blueberry
        System.Windows.Media.Color.FromRgb(0x9B, 0x6B, 0xD6), // grape
    ];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value?.ToString() ?? "";
        var hash = 17;
        foreach (var c in name.ToUpperInvariant()) hash = unchecked(hash * 31 + c); // stable across runs, unlike GetHashCode
        var top = Palette[(int)((uint)hash % Palette.Length)];
        var bottom = System.Windows.Media.Color.FromRgb((byte)(top.R * 0.78), (byte)(top.G * 0.78), (byte)(top.B * 0.78));
        var brush = new System.Windows.Media.LinearGradientBrush(top, bottom, 45);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
