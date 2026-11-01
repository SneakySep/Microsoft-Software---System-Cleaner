using System.Globalization;
using System.Windows.Data;

namespace WindowsCleaner.App.Converters;

/// <summary>
/// Renders a UTC timestamp as a short relative age such as <c>3d ago</c>.
/// </summary>
/// <remarks>
/// Age is more useful than an absolute date in this list: it is the cue the user needs to
/// judge whether a file is genuinely stale.
/// </remarks>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime utc)
        {
            return string.Empty;
        }

        var age = DateTime.UtcNow - DateTime.SpecifyKind(utc, DateTimeKind.Utc);

        return age switch
        {
            { TotalSeconds: < 60 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)age.TotalMinutes}m ago",
            { TotalHours: < 24 } => $"{(int)age.TotalHours}h ago",
            { TotalDays: < 30 } => $"{(int)age.TotalDays}d ago",
            { TotalDays: < 365 } => $"{(int)(age.TotalDays / 30)}mo ago",
            _ => $"{(int)(age.TotalDays / 365)}y ago",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
