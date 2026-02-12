using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace PartitionToolSharp.Desktop.Converters;

public class BitwiseAndToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is uint attr && parameter is string paramStr && uint.TryParse(paramStr, out var mask))
        {
            return (attr & mask) != 0;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // This is a bit complex for a simple converter as it needs the original value.
        // For now, let's keep it one-way or handle it in ViewModel if two-way is needed.
        return null;
    }
}
