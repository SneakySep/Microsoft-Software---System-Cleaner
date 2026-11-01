using System.Globalization;

namespace WindowsCleaner.Core.Scheduling;

/// <summary>
/// Shared rendering of schedule values.
/// </summary>
/// <remarks>
/// Separate from <see cref="ScheduledCleanTask"/> on purpose: that class is Windows-only, and the
/// pieces that merely describe a schedule are not. Keeping them here means a task snapshot can be
/// parsed and displayed on any platform.
/// </remarks>
public static class ScheduleText
{
    /// <summary>Formats a time of day as <c>HH:mm</c>, the way the scheduler stores it.</summary>
    public static string FormatTimeOfDay(TimeSpan time) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)time.TotalHours % 24:00}:{time.Minutes:00}");
}
