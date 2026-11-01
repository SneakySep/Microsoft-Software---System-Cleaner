namespace WindowsCleaner.Core.Models;

/// <summary>
/// What a finished clean did, in enough detail to compare against what it was given.
/// </summary>
/// <remarks>
/// <para>
/// Kept so the result is still visible after the fact — the next time the app starts, or after
/// an unattended run that nobody watched. Both front-ends record one, which is what lets a
/// scheduled 3am run show up in the dashboard in the morning.
/// </para>
/// <para>
/// The before/after split is deliberate. "Before" is what the clean was handed; "after" is what
/// actually happened to it, which can differ (files locked by another process, paths refused by
/// the guard rail). Storing only the totals would make a partially failed clean indistinguishable
/// from a complete one.
/// </para>
/// </remarks>
public sealed record CleanReport
{
    public required DateTimeOffset TimestampUtc { get; init; }

    public required DeletionMode Mode { get; init; }

    /// <summary>How many files the clean was handed.</summary>
    public required int AttemptedCount { get; init; }

    /// <summary>Their total size as measured at scan time.</summary>
    public required long AttemptedBytes { get; init; }

    /// <summary>Files removed from their location, whether deleted or recycled.</summary>
    public required int RemovedCount { get; init; }

    public required int DeletedCount { get; init; }

    public required int RecycledCount { get; init; }

    /// <summary>Bytes reclaimed on disk. Zero in recycle mode, where nothing is reclaimed yet.</summary>
    public required long FreedBytes { get; init; }

    /// <summary>Bytes moved to the Recycle Bin, where they still occupy the volume.</summary>
    public required long RecycledBytes { get; init; }

    public required int FailedCount { get; init; }

    public required TimeSpan Duration { get; init; }

    public RecycleBinStats RecycleBinBefore { get; init; } = RecycleBinStats.Empty;

    public RecycleBinStats RecycleBinAfter { get; init; } = RecycleBinStats.Empty;

    /// <summary>Which front-end ran the clean; see <see cref="CleanReportSources"/>.</summary>
    public string Source { get; init; } = CleanReportSources.App;

    /// <summary>Bytes that genuinely left the volume, which only a permanent clean achieves.</summary>
    public long ReclaimedBytes => FreedBytes;

    /// <summary>How much the Recycle Bin grew, i.e. space moved rather than reclaimed.</summary>
    public long RecycledIntoBinBytes => Math.Max(0, RecycleBinAfter.SizeBytes - RecycleBinBefore.SizeBytes);

    /// <summary>Builds a report from a finished clean plus the Recycle Bin readings around it.</summary>
    public static CleanReport From(
        CleanResult result,
        int attemptedCount,
        RecycleBinStats recycleBinBefore,
        RecycleBinStats recycleBinAfter,
        DateTimeOffset timestampUtc,
        string source)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new CleanReport
        {
            TimestampUtc = timestampUtc,
            Mode = result.Mode,
            AttemptedCount = attemptedCount,
            AttemptedBytes = result.AttemptedBytes,
            RemovedCount = result.RemovedCount,
            DeletedCount = result.DeletedCount,
            RecycledCount = result.RecycledCount,
            FreedBytes = result.FreedBytes,
            RecycledBytes = result.RecycledBytes,
            FailedCount = result.FailedCount,
            Duration = result.Duration,
            RecycleBinBefore = recycleBinBefore,
            RecycleBinAfter = recycleBinAfter,
            Source = source,
        };
    }
}
