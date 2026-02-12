using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace PartitionToolSharp.Desktop.Converters;

public class NumericLessThanConverter : IValueConverter, IMultiValueConverter
{
    public static readonly NumericLessThanConverter Instance = new();

    // Single value converter (for IsVisible binding etc)
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        // ... (existing logic)
        false;

    // Multi value converter (for comparing two properties)
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2 && values[0] is ulong val1 && values[1] is ulong val2)
        {
            if (val2 == 0)
            {
                return false; // FS size unknown
            }

            return val1 < val2;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
