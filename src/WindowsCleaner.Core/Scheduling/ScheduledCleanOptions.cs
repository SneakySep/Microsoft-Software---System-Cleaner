using WindowsCleaner.Core.Models;

namespace WindowsCleaner.Core.Scheduling;

public enum ScheduleFrequency
{
    Daily,
    Weekly,
}

/// <summary>How an unattended, scheduled clean should run.</summary>
public sealed class ScheduledCleanOptions
{
    /// <summary>
    /// The freshness guard unattended runs use.
    /// </summary>
    /// <remarks>
    /// A named constant because more than one place needs to state it: the CLI applies it, the
    /// app displays it as the fixed rule for scheduled cleaning, and a panel showing a registered
    /// job needs to notice when the job was created with a different one.
    /// </remarks>
    public static TimeSpan DefaultMinimumAge { get; } = TimeSpan.FromHours(24);

    public ScheduleFrequency Frequency { get; init; } = ScheduleFrequency.Daily;

    /// <summary>Day of week, used when <see cref="Frequency"/> is <see cref="ScheduleFrequency.Weekly"/>.</summary>
    public DayOfWeek Day { get; init; } = DayOfWeek.Sunday;

    public TimeSpan TimeOfDay { get; init; } = new(3, 0, 0);

    /// <summary>
    /// Disposal mode for scheduled runs.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="DeletionMode.Permanent"/>, deliberately the opposite of the
    /// interactive default. A scheduled clean runs unattended and repeatedly, so recycling
    /// would simply pile up in the Recycle Bin forever and never reclaim anything, which
    /// defeats the point of scheduling it. Safer scheduling comes from the min-age below
    /// rather than from the disposal mode.
    /// </remarks>
    public DeletionMode Mode { get; init; } = DeletionMode.Permanent;

    /// <summary>
    /// Freshness guard for scheduled runs.
    /// </summary>
    /// <remarks>
    /// A full day, far longer than the 15 minutes used interactively. An unattended job should
    /// only ever touch files that have provably sat untouched for a long time, so a temp file
    /// belonging to something that is merely idle overnight is not swept away.
    /// </remarks>
    public TimeSpan MinimumAge { get; init; } = DefaultMinimumAge;

    /// <summary>Executable to schedule. Defaults to the running process.</summary>
    public string? ExecutablePath { get; init; }
}
