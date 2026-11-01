using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Safety;
using WindowsCleaner.Core.Services;
using WindowsCleaner.Core.Targets;

namespace WindowsCleaner.Tests;

/// <summary>
/// An isolated directory tree used as a stand-in for <c>%TEMP%</c>.
/// </summary>
/// <remarks>
/// Every test gets its own uniquely named root, which keeps the suite safe to run in parallel
/// and means a test never touches the real temp folder. The whole pipeline is pointed at it
/// through <see cref="ICleanTargetProvider"/>, so the scanner and cleaner code under test is
/// exactly the production code path, just aimed somewhere harmless.
/// </remarks>
internal sealed class Sandbox : IDisposable
{
    public Sandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "wc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Creates a file of <paramref name="sizeBytes"/> bytes and returns its full path.</summary>
    public string CreateFile(string relativePath, int sizeBytes, DateTime? lastWriteUtc = null)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[sizeBytes]);

        if (lastWriteUtc is { } timestamp)
        {
            File.SetLastWriteTimeUtc(full, timestamp);
        }

        return full;
    }

    public string CreateDirectory(string relativePath)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>A timestamp old enough to clear any realistic freshness guard.</summary>
    public static DateTime Stale { get; } = DateTime.UtcNow.AddDays(-7);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked file from a test can keep the tree alive; it is under %TEMP% and
            // the OS will reclaim it, so failing cleanup must not fail the test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Points the engine at one directory for the duration of a test.</summary>
internal sealed class FixedTargetProvider(params string[] roots) : ICleanTargetProvider
{
    public IReadOnlyList<CleanTarget> GetTargets() =>
        roots
            .Select(root => new CleanTarget
            {
                DisplayName = "Sandbox",
                Path = PathGuard.NormalizeRoot(root),
                RequiresElevation = false,
            })
            .ToList();
}

internal static class TestPipeline
{
    /// <summary>
    /// Explicit disposal modes.
    /// </summary>
    /// <remarks>
    /// Every test states which mode it means rather than relying on the default. Deleting and
    /// recycling are materially different behaviours and a test that silently depends on the
    /// default would be testing the wrong thing the moment that default changes.
    /// </remarks>
    public static readonly CleanOptions Permanent = new() { Mode = DeletionMode.Permanent };

    public static readonly CleanOptions Recycle = new() { Mode = DeletionMode.RecycleBin };

    /// <summary>
    /// Builds a scanner and cleaner wired to <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// Defaults <see cref="ScannerOptions.MinimumAge"/> to zero so files the test just wrote
    /// are not filtered out as "too recent". Tests that specifically exercise the freshness
    /// guard pass their own options.
    /// </remarks>
    public static (TempScanner Scanner, CleanerService Cleaner) Create(
        string root,
        ScannerOptions? options = null)
    {
        var provider = new FixedTargetProvider(root);
        var effective = options ?? new ScannerOptions { MinimumAge = TimeSpan.Zero };

        return (new TempScanner(provider, effective), new CleanerService(provider, effective));
    }

    /// <summary>Builds the <see cref="JunkFile"/> a scan would have produced for a real file.</summary>
    public static JunkFile ToJunkFile(this string path)
    {
        var info = new FileInfo(path);
        return new JunkFile
        {
            FullPath = path,
            SizeBytes = info.Length,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
            TargetName = "Sandbox",
        };
    }
}
