using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace FMSquared;

public static class Converters
{
    public static readonly IValueConverter ByteSizeConverter = new ByteSizeToStringConverter();
    public static readonly IValueConverter RowMatchBackground = new BoolToBrushConverter("#FBF0C4", "White");
}

public class ByteSizeToStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long bytes && bytes >= 0)
        {
            var size = ByteSizeLib.ByteSize.FromBytes(bytes);
            if (size.GigaBytes >= 1)
                return $"{size.GigaBytes:F1} GB";
            if (size.MegaBytes >= 1)
                return $"{size.MegaBytes:F0} MB";
            return $"{size.KiloBytes:F0} KB";
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public class BoolToBrushConverter : IValueConverter
{
    private readonly IBrush _trueBrush;
    private readonly IBrush _falseBrush;

    public BoolToBrushConverter(string trueColor, string falseColor)
    {
        _trueBrush = new SolidColorBrush(Color.Parse(trueColor));
        _falseBrush = new SolidColorBrush(Color.Parse(falseColor));
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? _trueBrush : _falseBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
