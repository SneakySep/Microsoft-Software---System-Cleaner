using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Services;

namespace WindowsCleaner.Tests;

/// <summary>
/// Scanner behaviour, exercised against a real directory tree rather than a mocked file system
/// so the walking, sizing and error handling are actually covered.
/// </summary>
public sealed class TempScannerTests
{
    [Fact]
    public async Task Finds_files_recursively_and_sums_their_sizes()
    {
        using var sandbox = new Sandbox();
        sandbox.CreateFile("a.tmp", 100, Sandbox.Stale);
        sandbox.CreateFile("nested/b.tmp", 250, Sandbox.Stale);
        sandbox.CreateFile("nested/deeper/c.tmp", 400, Sandbox.Stale);

        var (scanner, _) = TestPipeline.Create(sandbox.Root);

        var result = await scanner.ScanAsync();

        Assert.Equal(3, result.Files.Count);
        Assert.Equal(750, result.TotalSizeBytes);
    }

    [Fact]
    public async Task Returns_the_largest_files_first()
    {
        using var sandbox = new Sandbox();
        sandbox.CreateFile("small.tmp", 10, Sandbox.Stale);
        sandbox.CreateFile("large.tmp", 5000, Sandbox.Stale);
        sandbox.CreateFile("medium.tmp", 900, Sandbox.Stale);

        var (scanner, _) = TestPipeline.Create(sandbox.Root);

        var result = await scanner.ScanAsync();

        Assert.Equal([5000L, 900L, 10L], result.Files.Select(f => f.SizeBytes));
    }

    [Fact]
    public async Task Reports_each_found_file_with_a_usable_path_and_size()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.CreateFile("thing.tmp", 42, Sandbox.Stale);

        var (scanner, _) = TestPipeline.Create(sandbox.Root);

        var result = await scanner.ScanAsync();

        var file = Assert.Single(result.Files);
        Assert.Equal(path, file.FullPath);
        Assert.Equal(42, file.SizeBytes);
        Assert.Equal("thing.tmp", file.FileName);
        Assert.True(File.Exists(file.FullPath));
    }

    [Fact]
    public async Task Never_reports_the_root_directory_itself()
    {
        using var sandbox = new Sandbox();
        sandbox.CreateFile("a.tmp", 10, Sandbox.Stale);

        var (scanner, _) = TestPipeline.Create(sandbox.Root);

        var result = await scanner.ScanAsync();

        Assert.DoesNotContain(result.Files, f => f.FullPath == sandbox.Root);
    }

    [Fact]
    public async Task Skips_files_modified_more_recently_than_the_minimum_age()
    {
        using var sandbox = new Sandbox();
        sandbox.CreateFile("old.tmp", 100, Sandbox.Stale);
        sandbox.CreateFile("brand-new.tmp", 100); // timestamp = now

        // This is the guard that stops the cleaner from eating a file an installer is mid-write on.
        var options = new ScannerOptions { MinimumAge = TimeSpan.FromMinutes(15) };
        var (scanner, _) = TestPipeline.Create(sandbox.Root, options);

        var result = await scanner.ScanAsync();

        var file = Assert.Single(result.Files);
        Assert.Equal("old.tmp", file.FileName);
        Assert.Equal(1, result.Skipped[SkipReason.TooRecent]);
    }

    [Fact]
    public async Task Scans_everything_when_the_minimum_age_guard_is_disabled()
    {
        using var sandbox = new Sandbox();
        sandbox.CreateFile("old.tmp", 100, Sandbox.Stale);
        sandbox.CreateFile("brand-new.tmp", 100);

        var options = new ScannerOptions { MinimumAge = TimeSpan.Zero };
        var (scanner, _) = TestPipeline.Create(sandbox.Root, options);

        var result = await scanner.ScanAsync();

        Assert.Equal(2, result.Files.Count);
        Assert.False(result.Skipped.ContainsKey(SkipReason.TooRecent));
    }

    [Fact]
    public async Task Reports_a_missing_target_as_inaccessible_instead_of_throwing()
    {
        using var sandbox = new Sandbox();
        var missing = Path.Combine(sandbox.Root, "does-not-exist");
        var (scanner, _) = TestPipeline.Create(missing);

        var result = await scanner.ScanAsync();

        Assert.Empty(result.Files);
        var summary = Assert.Single(result.Targets);
        Assert.False(summary.Accessible);
        Assert.NotNull(summary.Error);
    }

    [Fact]
    public async Task An_empty_directory_yields_an_empty_result_without_error()
    {
        using var sandbox = new Sandbox();
        var (scanner, _) = TestPipeline.Create(sandbox.Root);

        var result = await scanner.ScanAsync();

        Assert.Empty(result.Files);
        Assert.Equal(0, result.TotalSizeBytes);
        Assert.True(Assert.Single(result.Targets).Accessible);
    }

    [Fact]
    public async Task Searches_several_roots_and_labels_which_target_each_file_came_from()
    {
        using var first = new Sandbox();
        using var second = new Sandbox();
        first.CreateFile("one.tmp", 10, Sandbox.Stale);
        second.CreateFile("two.tmp", 20, Sandbox.Stale);

        var provider = new FixedTargetProvider(first.Root, second.Root);
        var scanner = new TempScanner(provider, new ScannerOptions { MinimumAge = TimeSpan.Zero });

        var result = await scanner.ScanAsync();

        Assert.Equal(2, result.Files.Count);
        Assert.Equal(2, result.Targets.Count);
        Assert.All(result.Files, f => Assert.Equal("Sandbox", f.TargetName));
    }

    [Fact]
    public async Task Honours_the_file_cap()
    {
        using var sandbox = new Sandbox();
        for (var i = 0; i < 30; i++)
        {
            sandbox.CreateFile($"f{i}.tmp", 10, Sandbox.Stale);
        }

        var options = new ScannerOptions { MinimumAge = TimeSpan.Zero, MaxFiles = 10 };
        var (scanner, _) = TestPipeline.Create(sandbox.Root, options);

        var result = await scanner.ScanAsync();

        Assert.Equal(10, result.Files.Count);
    }

    [Fact]
    public async Task Reports_progress_including_the_target_being_scanned()
    {
        using var sandbox = new Sandbox();
        sandbox.CreateFile("a.tmp", 10, Sandbox.Stale);

        var (scanner, _) = TestPipeline.Create(sandbox.Root);
        var updates = new List<ScanProgress>();

        // Progress<T> posts asynchronously; a synchronous collector keeps the test deterministic.
        var progress = new Progress<ScanProgress>(updates.Add);
        await scanner.ScanAsync(progress);

        // Give the posted callbacks a chance to run on this thread's context.
        await Task.Delay(50);

        Assert.Contains(updates, u => u.TargetName == "Sandbox");
    }

    [Fact]
    public async Task Cancellation_stops_the_scan()
    {
        using var sandbox = new Sandbox();
        for (var i = 0; i < 200; i++)
        {
            sandbox.CreateFile($"f{i}.tmp", 10, Sandbox.Stale);
        }

        var (scanner, _) = TestPipeline.Create(sandbox.Root);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scanner.ScanAsync(cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Does_not_descend_through_a_directory_symlink()
    {
        using var sandbox = new Sandbox();
        var realDirectory = sandbox.CreateDirectory("real");
        sandbox.CreateFile("real/inside.tmp", 100, Sandbox.Stale);

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(sandbox.Root, "link"), realDirectory);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // Creating a symlink needs SeCreateSymbolicLinkPrivilege (admin or Developer Mode).
            // Without it this behaviour cannot be exercised here; it is covered by the
            // PathGuard.IsReparsePoint test instead.
            return;
        }

        var (scanner, _) = TestPipeline.Create(sandbox.Root);

        var result = await scanner.ScanAsync();

        // The file is reachable exactly once, through the real path, never through the link.
        Assert.Single(result.Files);
        Assert.DoesNotContain(result.Files, f => f.FullPath.Contains("link", StringComparison.OrdinalIgnoreCase));
    }
}
