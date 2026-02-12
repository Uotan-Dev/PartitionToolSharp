using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace PartitionToolSharp.Desktop.Converters;

public class EqualityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value != null && parameter != null && value.ToString() == parameter.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
