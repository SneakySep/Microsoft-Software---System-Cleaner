namespace WindowsCleaner.Core.Services;

/// <summary>Tunable knobs for a scan.</summary>
public sealed class ScannerOptions
{
    /// <summary>
    /// Files modified more recently than this are treated as in-use and skipped.
    /// </summary>
    /// <remarks>
    /// This is the single most valuable safety default in a temp cleaner. Applications
    /// (and installers, and Windows itself) constantly create short-lived files in the
    /// temp directory and expect them to survive for the duration of their work. Deleting
    /// a file that was written two seconds ago is how a cleaner corrupts an in-flight
    /// install. Fifteen minutes is comfortably longer than any normal transient.
    /// Skipped files are counted under <c>SkipReason.TooRecent</c> so this is never invisible.
    /// Set to <see cref="TimeSpan.Zero"/> to disable the guard.
    /// </remarks>
    public TimeSpan MinimumAge { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Hard cap on the number of files collected in one scan, as a guard against a
    /// pathological directory tree exhausting memory.
    /// </summary>
    public int MaxFiles { get; init; } = 500_000;

    /// <summary>
    /// Maximum directory depth to descend. Temp trees are shallow; this stops
    /// runaway traversal if a target ever contains a deep pathological structure.
    /// </summary>
    public int MaxDepth { get; init; } = 32;

    /// <summary>Minimum number of newly found files between progress reports.</summary>
    public int ProgressFileInterval { get; init; } = 250;

    /// <summary>Minimum wall-clock gap between progress reports.</summary>
    public TimeSpan ProgressTimeInterval { get; init; } = TimeSpan.FromMilliseconds(120);

    public static ScannerOptions Default { get; } = new();
}
