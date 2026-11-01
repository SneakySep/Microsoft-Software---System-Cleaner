using WindowsCleaner.Core.Models;

namespace WindowsCleaner.Tests;

/// <summary>
/// The distinction the dashboard leans on: space that moved into the Recycle Bin has not been
/// reclaimed, and reporting it as reclaimed would misstate the user's free disk space.
/// </summary>
public sealed class CleanReportTests
{
    [Fact]
    public void A_recycled_clean_reclaims_nothing_and_grows_the_bin()
    {
        var report = Report(
            mode: DeletionMode.RecycleBin,
            freedBytes: 0,
            recycledBytes: 5_000_000,
            binBefore: new RecycleBinStats(1_000, 2),
            binAfter: new RecycleBinStats(5_001_000, 12));

        Assert.Equal(0, report.ReclaimedBytes);
        Assert.Equal(5_000_000, report.RecycledIntoBinBytes);
    }

    [Fact]
    public void A_permanent_clean_reports_the_bytes_it_freed()
    {
        var report = Report(
            mode: DeletionMode.Permanent,
            freedBytes: 5_000_000,
            recycledBytes: 0,
            binBefore: new RecycleBinStats(1_000, 2),
            binAfter: new RecycleBinStats(1_000, 2));

        Assert.Equal(5_000_000, report.ReclaimedBytes);

        // Permanent deletion cannot make the bin grow.
        Assert.Equal(0, report.RecycledIntoBinBytes);
    }

    [Fact]
    public void A_bin_that_shrank_is_never_reported_as_growing()
    {
        var report = Report(
            mode: DeletionMode.Permanent,
            freedBytes: 10,
            recycledBytes: 0,
            binBefore: new RecycleBinStats(9_000, 9),
            binAfter: new RecycleBinStats(1_000, 1));

        Assert.Equal(0, report.RecycledIntoBinBytes);
    }

    private static CleanReport Report(
        DeletionMode mode,
        long freedBytes,
        long recycledBytes,
        RecycleBinStats binBefore,
        RecycleBinStats binAfter) => new()
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Mode = mode,
            AttemptedCount = 10,
            AttemptedBytes = freedBytes + recycledBytes,
            RemovedCount = 10,
            DeletedCount = mode == DeletionMode.Permanent ? 10 : 0,
            RecycledCount = mode == DeletionMode.RecycleBin ? 10 : 0,
            FreedBytes = freedBytes,
            RecycledBytes = recycledBytes,
            FailedCount = 0,
            Duration = TimeSpan.FromSeconds(1),
            RecycleBinBefore = binBefore,
            RecycleBinAfter = binAfter,
        };
}
