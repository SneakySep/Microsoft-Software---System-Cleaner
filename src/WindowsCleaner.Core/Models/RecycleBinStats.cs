namespace WindowsCleaner.Core.Models;

/// <summary>How much the Windows Recycle Bin is currently holding.</summary>
/// <remarks>
/// Worth surfacing because recycled files still occupy the volume: until the bin is emptied,
/// a "clean" has moved space around rather than reclaimed it.
/// </remarks>
public readonly record struct RecycleBinStats(long SizeBytes, long ItemCount)
{
    public bool IsEmpty => ItemCount <= 0;

    public static RecycleBinStats Empty { get; } = new(0, 0);

    /// <summary>
    /// Whether the bin is holding enough to be worth offering to empty.
    /// </summary>
    /// <remarks>
    /// Below the threshold it is left alone deliberately. Emptying is irreversible, and a bin
    /// holding a few hundred megabytes is usually something the user is keeping on purpose for a
    /// day or two before restoring or discarding it. An empty bin never qualifies, whatever the
    /// threshold, because there is nothing to destroy.
    /// </remarks>
    public bool MeetsEmptyThreshold(long thresholdBytes) =>
        ItemCount > 0 && SizeBytes >= thresholdBytes;
}

/// <summary>The outcome of emptying the Recycle Bin.</summary>
public sealed record RecycleBinEmptyResult(
    bool Succeeded,
    long FreedBytes,
    long ItemsRemoved,
    RecycleBinStats Before,
    RecycleBinStats After,
    string? Error)
{
    /// <summary>
    /// How the bin's contents changed, measured by the shell's own accounting rather than by
    /// assuming the whole of <see cref="Before"/> was removed: a file locked by another process
    /// survives an empty, and reporting it as reclaimed would be a lie about disk space.
    /// </summary>
    public static RecycleBinEmptyResult Failure(RecycleBinStats before, string error) =>
        new(false, 0, 0, before, before, error);
}
