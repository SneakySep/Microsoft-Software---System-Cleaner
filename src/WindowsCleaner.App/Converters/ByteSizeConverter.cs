using System.Globalization;
using System.Windows.Data;
using WindowsCleaner.Core.Utilities;

namespace WindowsCleaner.App.Converters;

/// <summary>Renders a byte count using the shared <see cref="ByteSizeFormatter"/>.</summary>
public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long bytes ? ByteSizeFormatter.Format(bytes) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
