using System.Globalization;

namespace WindowsCleaner.App;

/// <summary>Renders durations and ages the way the UI talks about them.</summary>
internal static class Humanize
{
    /// <summary>Short relative age such as <c>3h ago</c>.</summary>
    public static string Age(TimeSpan age) => age switch
    {
        { TotalSeconds: < 60 } => "just now",
        { TotalMinutes: < 60 } => $"{age.TotalMinutes:0}m ago",
        { TotalHours: < 24 } => $"{age.TotalHours:0}h ago",
        { TotalDays: < 30 } => $"{age.TotalDays:0}d ago",
        { TotalDays: < 365 } => $"{age.TotalDays / 30:0}mo ago",
        _ => $"{age.TotalDays / 365:0}y ago",
    };

    /// <summary>Spelled-out duration such as <c>24 hours</c>, for stating a rule rather than an age.</summary>
    public static string Duration(TimeSpan value) => value switch
    {
        { TotalHours: >= 48 } => string.Create(CultureInfo.InvariantCulture, $"{value.TotalDays:0.#} days"),
        { TotalHours: >= 1 } => string.Create(CultureInfo.InvariantCulture, $"{value.TotalHours:0.#} hours"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{value.TotalMinutes:0} minutes"),
    };
}
