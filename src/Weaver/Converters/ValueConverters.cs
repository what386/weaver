using System;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Weaver.Models;

namespace Weaver.Converters;

public class Base64ToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string base64String || string.IsNullOrWhiteSpace(base64String))
            return null;

        try
        {
            var bytes = System.Convert.FromBase64String(base64String);
            using var stream = new System.IO.MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class HexToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrWhiteSpace(hex))
            return new SolidColorBrush(Colors.White);

        try
        {
            // Remove # if present
            hex = hex.TrimStart('#');

            // Parse RGB values
            if (hex.Length == 6)
            {
                var r = System.Convert.ToByte(hex.Substring(0, 2), 16);
                var g = System.Convert.ToByte(hex.Substring(2, 2), 16);
                var b = System.Convert.ToByte(hex.Substring(4, 2), 16);
                return new SolidColorBrush(Color.FromRgb(r, g, b));
            }

            return new SolidColorBrush(Colors.White);
        }
        catch
        {
            return new SolidColorBrush(Colors.White);
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class TotalWeightConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ThreeMFJob job)
            return job.Filaments.Sum(f => f.WeightGrams);

        if (value is Filament[] filaments)
            return filaments.Sum(f => f.WeightGrams);

        return 0.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
