namespace WindowsCleaner.Core.Models;

/// <summary>A file that could not be deleted, and why.</summary>
public sealed record CleanFailure(string Path, string Reason);

/// <summary>The outcome of a clean operation.</summary>
public sealed class CleanResult
{
    /// <summary>Which disposal mode the run used.</summary>
    public DeletionMode Mode { get; init; }

    /// <summary>
    /// Bytes actually reclaimed on disk.
    /// </summary>
    /// <remarks>
    /// This is the logical file length, not the cluster-rounded allocation size the volume
    /// reports. For a "you freed 1.2 GB" figure the logical size is the honest and
    /// conventional number, but it is worth knowing the two can differ slightly.
    /// <para>
    /// Zero in <see cref="DeletionMode.RecycleBin"/> mode: a recycled file still occupies the
    /// volume. Its bytes are reported in <see cref="RecycledBytes"/> until the bin is emptied.
    /// </para>
    /// </remarks>
    public long FreedBytes { get; init; }

    /// <summary>Bytes moved to the Recycle Bin. Zero in permanent mode.</summary>
    public long RecycledBytes { get; init; }

    public int DeletedCount { get; init; }

    public int RecycledCount { get; init; }

    public IReadOnlyList<CleanFailure> Failures { get; init; } = [];

    public TimeSpan Duration { get; init; }

    public int FailedCount => Failures.Count;

    /// <summary>Total bytes the scan attributed to the files we attempted.</summary>
    public long AttemptedBytes { get; init; }

    /// <summary>Files removed from their location, whether deleted or recycled.</summary>
    public int RemovedCount => DeletedCount + RecycledCount;
}
