using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuantAnalyst.Desktop.Core.Presentation;
using QuantAnalyst.Desktop.Core.ViewModels;

namespace QuantAnalyst.Desktop;

/// <summary>PNG bytes (the BankID QR code) to an image; null stays null.</summary>
public sealed class PngToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } png)
        {
            return null;
        }

        var image = new BitmapImage();
        using var stream = new MemoryStream(png);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// The theme's colour for a state word (a status mark, a report state, an order state, a tone):
/// ok/CLEAN/Filled/up green, todo/Working/info blue, warn/INCOMPLETE/partly filled amber, FAIL/NOT CLEAN/Rejected/down
/// red, anything else grey. The strong colour for text; <see cref="MarkToSoftBrushConverter"/> gives the chip's tint.
/// </summary>
public sealed class MarkToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Theme.Brush(Tone.Of(value as string), soft: false);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The light tint behind a state chip (see <see cref="MarkToBrushConverter"/>).</summary>
public sealed class MarkToSoftBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Theme.Brush(Tone.Of(value as string), soft: true);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True to <see cref="Visibility.Collapsed"/>, false to visible (the opposite of the built-in converter).</summary>
public sealed class InverseVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True to false and false to true (e.g. "enabled while not busy").</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// A non-empty text (or a count above zero) is visible; empty, zero or null is collapsed. With the parameter
/// "inverse" it is the other way round (e.g. "No orders yet" while the count is zero).
/// </summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool present = value switch
        {
            string s => s.Length > 0,
            int n => n > 0,
            null => false,
            _ => true,
        };

        bool show = parameter as string == "inverse" ? !present : present;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A page to its navigation icon (the "Icon…" geometries in Themes/Theme.xaml).</summary>
public sealed class PageIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is PageKind kind
        ? Application.Current?.TryFindResource(kind switch
        {
            PageKind.Status => "IconOverview",
            PageKind.Session => "IconTrading",
            PageKind.Accounts => "IconAccounts",
            PageKind.Instruments => "IconInstruments",
            PageKind.Strategy => "IconStrategy",
            _ => "IconReports",
        }) as Geometry
        : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The theme's brushes by tone, looked up in Themes/Theme.xaml so colours live in one place.</summary>
internal static class Theme
{
    public static Brush Brush(ToneKind tone, bool soft)
    {
        string key = tone switch
        {
            ToneKind.Positive => "Positive",
            ToneKind.Info => "Info",
            ToneKind.Warning => "Warning",
            ToneKind.Negative => "Negative",
            _ => "Line",
        };

        string name = key == "Line" ? (soft ? "SurfaceAltBrush" : "InkSecondaryBrush") : key + (soft ? "SoftBrush" : "Brush");
        return Application.Current?.TryFindResource(name) as Brush ?? Brushes.Gray;
    }
}
