using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Safety;
using WindowsCleaner.Core.Services;

namespace WindowsCleaner.Tests;

/// <summary>
/// Disposal behaviour. These tests actually create and remove files, because the failure modes
/// that matter here (sharing violations, read-only attributes, TOCTOU races, shell recycling)
/// only exist on a real file system and cannot be reproduced with a mock.
/// </summary>
public sealed class CleanerServiceTests
{
    // ============================ Permanent mode ============================

    [Fact]
    public async Task Deletes_a_file_and_reports_the_freed_bytes()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.CreateFile("junk.tmp", 1024, Sandbox.Stale);
        var (_, cleaner) = TestPipeline.Create(sandbox.Root);

        var result = await cleaner.CleanAsync([path.ToJunkFile()], TestPipeline.Permanent);

        Assert.Equal(DeletionMode.Permanent, result.Mode);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(1024, result.FreedBytes);
        Assert.Equal(0, result.RecycledBytes);
        Assert.Empty(result.Failures);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Sums_the_freed_bytes_across_many_files()
    {
        using var sandbox = new Sandbox();
        var files = new[]
        {
            sandbox.CreateFile("a.tmp", 100, Sandbox.Stale),
            sandbox.CreateFile("sub/b.tmp", 200, Sandbox.Stale),
            sandbox.CreateFile("sub/c.tmp", 300, Sandbox.Stale),
        };
        var (_, cleaner) = TestPipeline.Create(sandbox.Root);

        var result = await cleaner.CleanAsync(
            files.Select(TestPipeline.ToJunkFile).ToList(),
            TestPipeline.Permanent);

        Assert.Equal(3, result.DeletedCount);
        Assert.Equal(600, result.FreedBytes);
        Assert.All(files, f => Assert.False(File.Exists(f)));
    }

    /// <summary>
    /// The behaviour the whole app hinges on: a file another process holds open must be
    /// skipped and reported, never crash the clean and never be force-removed.
    /// </summary>
    [Fact]
    public async Task Skips_a_file_locked_by_another_process_without_throwing()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.CreateFile("locked.tmp", 500, Sandbox.Stale);
        var (_, cleaner) = TestPipeline.Create(sandbox.Root);

        // FileShare.None reproduces a file an active process is writing to.
        using (var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await cleaner.CleanAsync([path.ToJunkFile()], TestPipeline.Permanent);

            Assert.Equal(0, result.DeletedCount);
            Assert.Equal(0, result.FreedBytes);
            var failure = Assert.Single(result.Failures);
            Assert.Equal(path, failure.Path);
            Assert.Contains("in use", failure.Reason, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(File.Exists(path), "a locked file must survive");
    }

    [Fact]
    public async Task Keeps_going_when_some_files_are_locked_and_others_are_not()
    {
        using var sandbox = new Sandbox();
        var lockedPath = sandbox.CreateFile("locked.tmp", 100, Sandbox.Stale);
        var freePath = sandbox.CreateFile("free.tmp", 250, Sandbox.Stale);
        var (_, cleaner) = TestPipeline.Create(sandbox.Root);

        using (var _ = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await cleaner.CleanAsync(
                [lockedPath.ToJunkFile(), freePath.ToJunkFile()],
                TestPipeline.Permanent);

            Assert.Equal(1, result.DeletedCount);
            Assert.Equal(250, result.FreedBytes);
            Assert.Equal(1, result.FailedCount);
        }

        Assert.True(File.Exists(lockedPath));
        Assert.False(File.Exists(freePath));
    }

    [Fact]
    public async Task Refuses_to_delete_a_file_outside_the_allowlisted_roots()
    {
        using var allowed = new Sandbox();
        using var elsewhere = new Sandbox();

        var outsider = elsewhere.CreateFile("precious.txt", 100, Sandbox.Stale);
        var (_, cleaner) = TestPipeline.Create(allowed.Root);

        // Hand the cleaner a list it did not produce. The delete-time allowlist check must catch it.
        var result = await cleaner.CleanAsync([outsider.ToJunkFile()], TestPipeline.Permanent);

        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(0, result.FreedBytes);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("Refused", failure.Reason);
        Assert.True(File.Exists(outsider), "a file outside the roots must never be touched");
    }

    [Fact]
    public async Task Deletes_a_read_only_file()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.CreateFile("readonly.tmp", 128, Sandbox.Stale);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var (_, cleaner) = TestPipeline.Create(sandbox.Root);

        var result = await cleaner.CleanAsync([path.ToJunkFile()], TestPipeline.Permanent);

        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(128, result.FreedBytes);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Reports_a_file_that_vanished_before_the_clean()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.CreateFile("gone.tmp", 64, Sandbox.Stale);
        var junk = path.ToJunkFile();
        File.Delete(path); // Simulates another process removing it between scan and clean.

        var (_, cleaner) = TestPipeline.Create(sandbox.Root);
        var result = await cleaner.CleanAsync([junk], TestPipeline.Permanent);

        Assert.Equal(0, result.DeletedCount);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("exists", failure.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reported totals must come from the file as it is at delete time, not from the scan snapshot.
    /// </summary>
    [Fact]
    public async Task Reports_the_live_size_rather_than_the_size_recorded_at_scan_time()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.CreateFile("changed.tmp", 100, Sandbox.Stale);
        var (_, cleaner) = TestPipeline.Create(sandbox.Root);

        var staleRecord = new JunkFile
        {
            FullPath = path,
            SizeBytes = 999_999, // Deliberately wrong.
            LastWriteTimeUtc = Sandbox.Stale,
            TargetName = "Sandbox",
        };

        var result = await cleaner.CleanAsync([staleRecord], TestPipeline.Permanent);

        Assert.Equal(100, result.FreedBytes);
    }

    [Fact]
    public async Task Skips_a_file_that_became_too_recent_since_the_scan()
    {
        using var sandbox = new Sandbox();

        // Written just now, so its real timestamp is current.
        var path = sandbox.CreateFile("fresh.tmp", 200);

        // A record claiming the file is stale, while the file itself is brand new.
        var record = new JunkFile
        {
            FullPath = path,
            SizeBytes = 200,
            LastWriteTimeUtc = Sandbox.Stale,
            TargetName = "Sandbox",
        };

        var options = new ScannerOptions { MinimumAge = TimeSpan.FromMinutes(15) };
        var (_, cleaner) = TestPipeline.Create(sandbox.Root, options);

        var result = await cleaner.CleanAsync([record], TestPipeline.Permanent);

        Assert.Equal(0, result.DeletedCount);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task An_empty_list_is_a_no_op()
    {
        using var sandbox = new Sandbox();
        var (_, cleaner) = TestPipeline.Create(sandbox.Root);

        var result = await cleaner.CleanAsync([], TestPipeline.Permanent);

        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(0, result.FreedBytes);
        Assert.Empty(result.Failures);
    }

    /// <summary>
    /// The end-to-end contract: everything a scan reports can be removed by a clean, and the
    /// reported total matches what actually disappeared.
    /// </summary>
    [Fact]
    public async Task End_to_end_scan_then_clean_removes_exactly_what_was_reported()
    {
        using var sandbox = new Sandbox();
        var expected = new[]
        {
            sandbox.CreateFile("a.tmp", 1024, Sandbox.Stale),
            sandbox.CreateFile("sub/b.tmp", 2048, Sandbox.Stale),
            sandbox.CreateFile("sub/deeper/c.tmp", 4096, Sandbox.Stale),
        };

        var (scanner, cleaner) = TestPipeline.Create(sandbox.Root);

        var scan = await scanner.ScanAsync();
        Assert.Equal(3, scan.Files.Count);
        Assert.Equal(7168, scan.TotalSizeBytes);

        var clean = await cleaner.CleanAsync(scan.Files, TestPipeline.Permanent);

        Assert.Equal(3, clean.DeletedCount);
        Assert.Equal(7168, clean.FreedBytes);
        Assert.Equal(scan.TotalSizeBytes, clean.FreedBytes);
        Assert.Empty(clean.Failures);
        Assert.All(expected, p => Assert.False(File.Exists(p)));
    }

    // =========================== Recycle Bin mode ===========================

    /// <summary>
    /// Proves files genuinely reach the Recycle Bin rather than merely leaving their original
    /// location, which a permanent delete would also accomplish. The shell's own item count is
    /// the evidence.
    /// </summary>
    /// <remarks>
    /// This leaves one small file in the real Recycle Bin (it cannot be removed again without
    /// risking the user's other recycled files). The file is named distinctively so it is easy
    /// to spot.
    /// </remarks>
    [Fact]
    public async Task Moves_a_file_to_the_recycle_bin_rather_than_deleting_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new Sandbox();
        var path = sandbox.CreateFile("windows-cleaner-test.tmp", 4096, Sandbox.Stale);
        var (_, cleaner) = TestPipeline.Create(sandbox.Root);

        var before = RecycleBinInfo.Query();

        var result = await cleaner.CleanAsync([path.ToJunkFile()], TestPipeline.Recycle);

        var after = RecycleBinInfo.Query();

        Assert.Equal(DeletionMode.RecycleBin, result.Mode);
        Assert.Equal(1, result.RecycledCount);
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(4096, result.RecycledBytes);
        Assert.Empty(result.Failures);

        // The file has left its original location...
        Assert.False(File.Exists(path));

        // ...and the Recycle Bin grew to receive it.
        Assert.True(
            after.ItemCount >= before.ItemCount + 1,
            $"expected the recycle bin to gain an item, but it went from {before.ItemCount} to {after.ItemCount}");
    }

    [Fact]
    public async Task Recycle_mode_does_not_report_space_as_freed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new Sandbox();
        var path = sandbox.CreateFile("windows-cleaner-test-space.tmp", 2048, Sandbox.Stale);
        var (_, cleaner) = TestPipeline.Create(sandbox.Root);

        var result = await cleaner.CleanAsync([path.ToJunkFile()], TestPipeline.Recycle);

        // A recycled file still occupies the volume, so claiming freed space here would be a lie.
        Assert.Equal(0, result.FreedBytes);
        Assert.Equal(2048, result.RecycledBytes);
        Assert.Equal(1, result.RemovedCount);
    }

    [Fact]
    public async Task Recycle_mode_still_enforces_the_allowlist()
    {
        using var allowed = new Sandbox();
        using var elsewhere = new Sandbox();

        var outsider = elsewhere.CreateFile("precious.txt", 100, Sandbox.Stale);
        var (_, cleaner) = TestPipeline.Create(allowed.Root);

        var result = await cleaner.CleanAsync([outsider.ToJunkFile()], TestPipeline.Recycle);

        Assert.Equal(0, result.RemovedCount);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("Refused", failure.Reason);
        Assert.True(File.Exists(outsider), "the allowlist must hold in every mode");
    }

    [Fact]
    public async Task Recycle_mode_completes_without_throwing_when_a_file_is_locked()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.CreateFile("locked-recycle.tmp", 300, Sandbox.Stale);
        var (_, cleaner) = TestPipeline.Create(sandbox.Root);

        CleanResult result;
        using (var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await cleaner.CleanAsync([path.ToJunkFile()], TestPipeline.Recycle);
        }

        // Whatever the shell decides, the contract is: no exception, and the file is accounted
        // for in exactly one bucket. A file held open must never be silently destroyed.
        Assert.Equal(1, result.RemovedCount + result.FailedCount);
        Assert.True(File.Exists(path), "a locked file must survive recycling too");
    }

    [Fact]
    public async Task Recycle_mode_handles_many_files_in_batches()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new Sandbox();

        // A deliberately tiny batch size so crossing the boundary takes a handful of files
        // rather than the default 64, keeping the test fast and the Recycle Bin litter small.
        var options = new CleanOptions { Mode = DeletionMode.RecycleBin, RecycleBatchSize = 5 };

        var files = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            files.Add(sandbox.CreateFile($"windows-cleaner-batch-{i}.tmp", 16, Sandbox.Stale));
        }

        var (_, cleaner) = TestPipeline.Create(sandbox.Root);

        var result = await cleaner.CleanAsync(
            files.Select(TestPipeline.ToJunkFile).ToList(),
            options);

        Assert.Equal(12, result.RecycledCount);
        Assert.Equal(12 * 16, result.RecycledBytes);
        Assert.Empty(result.Failures);
        Assert.All(files, f => Assert.False(File.Exists(f)));
    }
}
