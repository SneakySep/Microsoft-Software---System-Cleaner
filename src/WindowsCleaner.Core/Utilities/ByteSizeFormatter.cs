using System.Globalization;

namespace WindowsCleaner.Core.Utilities;

/// <summary>Formats byte counts the way a user expects to read disk space.</summary>
public static class ByteSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Formats <paramref name="bytes"/> with binary units (1024-based), which is the
    /// convention Windows Explorer uses for file sizes.
    /// </summary>
    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "-" + Format(-bytes);
        }

        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        }

        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // One decimal place keeps the figure precise without implying false accuracy.
        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {Units[unit]}");
    }
}
