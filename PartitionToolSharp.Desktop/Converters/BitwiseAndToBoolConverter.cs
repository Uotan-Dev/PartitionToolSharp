using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace PartitionToolSharp.Desktop.Converters;

public class BitwiseAndToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => parameter is string paramStr && ulong.TryParse(paramStr, out var mask) && (value is uint u32 ? (u32 & mask) != 0 : value is ulong u64 && (u64 & mask) != 0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        // This is a bit complex for a simple converter as it needs the original value.
        // For now, let's keep it one-way or handle it in ViewModel if two-way is needed.
        null;
}
