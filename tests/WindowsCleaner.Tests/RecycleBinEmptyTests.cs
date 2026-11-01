using WindowsCleaner.Core.Models;

namespace WindowsCleaner.Tests;

/// <summary>
/// When emptying the Recycle Bin is offered at all.
/// </summary>
/// <remarks>
/// Only the rule is tested here, not the emptying itself. Emptying destroys the whole bin with no
/// way to scope it to particular files, so a test that ran it would take the user's own recycled
/// files with it; that call is verified by hand instead, against a bin whose contents are known.
/// </remarks>
public sealed class RecycleBinEmptyTests
{
    private const long OneGigabyte = 1024L * 1024 * 1024;

    [Fact]
    public void An_empty_bin_is_never_worth_emptying()
    {
        Assert.False(RecycleBinStats.Empty.MeetsEmptyThreshold(OneGigabyte));
        Assert.False(RecycleBinStats.Empty.MeetsEmptyThreshold(0));
    }

    [Fact]
    public void A_bin_below_the_threshold_is_left_alone()
    {
        var stats = new RecycleBinStats(500 * 1024 * 1024, 40);

        Assert.False(stats.MeetsEmptyThreshold(OneGigabyte));
    }

    [Fact]
    public void A_bin_at_or_above_the_threshold_qualifies()
    {
        Assert.True(new RecycleBinStats(OneGigabyte, 1).MeetsEmptyThreshold(OneGigabyte));
        Assert.True(new RecycleBinStats(OneGigabyte * 3, 900).MeetsEmptyThreshold(OneGigabyte));
    }

    [Fact]
    public void A_zero_threshold_allows_any_non_empty_bin()
    {
        // The threshold is a preference, not a safety mechanism: it can be turned off entirely.
        Assert.True(new RecycleBinStats(1, 1).MeetsEmptyThreshold(0));
    }

    [Fact]
    public void A_threshold_of_zero_still_does_not_make_an_empty_bin_emptiable()
    {
        Assert.False(new RecycleBinStats(0, 0).MeetsEmptyThreshold(0));
    }

    [Fact]
    public void A_failed_emptying_reports_the_bin_as_unchanged()
    {
        var before = new RecycleBinStats(4_000_000, 20);

        var result = RecycleBinEmptyResult.Failure(before, "Access denied by the shell");

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.FreedBytes);
        Assert.Equal(0, result.ItemsRemoved);
        Assert.Equal(before, result.Before);
        Assert.Equal(before, result.After);
        Assert.Equal("Access denied by the shell", result.Error);
    }
}
