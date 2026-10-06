using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PizzaHeroClicker.Models;

namespace PizzaHeroClicker.Converters;

/// <summary>Zero-based index to a one-based label ("1", "2", ...).</summary>
public sealed class IndexPlusOneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i ? (i + 1).ToString(culture) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// The one-based position of an item in a list: values[0] is the item, values[1] the list.
/// Any further values are ignored; they exist so the binding re-runs when the list changes.
/// </summary>
public sealed class ItemNumberConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[1] is not System.Collections.IList list) return "";
        int index = list.IndexOf(values[0]);
        return index < 0 ? "" : (index + 1).ToString(culture);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>"#RRGGBB" text to a brush, for colour swatches. Invalid text gives a transparent brush.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        PixelColor.TryParse(value as string, out var c)
            ? new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B))
            : Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Base64 PNG text (an area watch's target picture) to an image source for thumbnails.</summary>
public sealed class Base64ToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        PizzaHeroClicker.Services.TemplateImage.ToBitmap(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the bound value's text equals any of the '|'-separated names in the parameter.</summary>
public sealed class MatchToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string actual = value?.ToString() ?? "";
        bool match = (parameter as string ?? "").Split('|').Contains(actual);
        return match ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Collapses an element when the bound string is empty.</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
