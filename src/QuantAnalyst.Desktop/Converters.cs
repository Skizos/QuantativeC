using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
/// A status mark or a report state to its colour: ok/CLEAN green, todo blue, warn/INCOMPLETE amber, FAIL/NOT CLEAN red.
/// </summary>
public sealed class MarkToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Green = Frozen(0x2E, 0x7D, 0x32);
    private static readonly SolidColorBrush Blue = Frozen(0x15, 0x65, 0xC0);
    private static readonly SolidColorBrush Amber = Frozen(0xB2, 0x6A, 0x00);
    private static readonly SolidColorBrush Red = Frozen(0xC6, 0x28, 0x28);
    private static readonly SolidColorBrush Grey = Frozen(0x61, 0x61, 0x61);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string) switch
    {
        "ok" or "CLEAN" => Green,
        "todo" => Blue,
        "warn" or "INCOMPLETE" => Amber,
        "FAIL" or "NOT CLEAN" => Red,
        _ => Grey,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>True to <see cref="Visibility.Collapsed"/>, false to visible (the opposite of the built-in converter).</summary>
public sealed class InverseVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A message's error flag to its colour: red for errors, dark green otherwise.</summary>
public sealed class ErrorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Brushes.Firebrick : Brushes.DarkGreen;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True to false and false to true (e.g. "enabled while not busy").</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
