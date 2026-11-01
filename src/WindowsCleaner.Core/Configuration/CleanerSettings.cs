namespace WindowsCleaner.Core.Configuration;

/// <summary>User settings that persist between runs.</summary>
public sealed class CleanerSettings
{
    /// <summary>
    /// Default for <see cref="RecycleBinEmptyThresholdBytes"/>: one gigabyte.
    /// </summary>
    public const long DefaultRecycleBinEmptyThresholdBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// Extra folders the user has chosen to include alongside the standard Windows temp
    /// locations.
    /// </summary>
    /// <remarks>
    /// These are additive. A user folder never replaces the built-in set, so adding a folder
    /// cannot silently stop the standard locations from being cleaned.
    /// </remarks>
    public List<string> ExtraFolders { get; set; } = [];

    /// <summary>
    /// How much the Recycle Bin must be holding before the app offers to empty it.
    /// </summary>
    /// <remarks>
    /// Emptying is irreversible and can only be done to the whole bin, so it is gated rather
    /// than offered constantly. A bin below this size is left alone; lower the value (or set it
    /// to 0) to allow emptying smaller ones.
    /// </remarks>
    public long RecycleBinEmptyThresholdBytes { get; set; } = DefaultRecycleBinEmptyThresholdBytes;

    /// <summary>
    /// Path to <c>wclean.exe</c>, remembered after the user picks it by hand.
    /// </summary>
    /// <remarks>
    /// Only used when the automatic search cannot find the command-line tool. Worth remembering
    /// because the alternative is picking the same file again every time a scheduled job is
    /// set up.
    /// </remarks>
    public string? CliExecutablePath { get; set; }
}
