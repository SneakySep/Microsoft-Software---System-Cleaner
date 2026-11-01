using System.Diagnostics;
using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Safety;
using WindowsCleaner.Core.Targets;

namespace WindowsCleaner.Core.Services;

/// <summary>
/// Disposes of disposable files, tolerating the locked and protected entries that are
/// unavoidable in a live <c>%TEMP%</c> directory.
/// </summary>
/// <remarks>
/// <para><b>Why revalidate.</b> The file list arrives from an earlier scan, so it is untrusted
/// input by this point: it may be minutes old, and any caller could hand over an arbitrary
/// list. Re-running <see cref="PathGuard"/> here, rather than trusting the scanner's earlier
/// verdict, is what makes it safe to expose this method. This also closes the
/// time-of-check/time-of-use window between scanning a path and deleting it.</para>
///
/// <para><b>Why re-stat.</b> Sizes and timestamps in the list are from scan time. The real
/// file is measured again so the reported totals reflect what was actually removed and a file
/// that was replaced by something newer is not silently destroyed.</para>
///
/// <para><b>Two disposal modes.</b> <see cref="DeletionMode.Permanent"/> uses <c>File.Delete</c>.
/// <see cref="DeletionMode.RecycleBin"/> goes through the shell, which is far slower per call,
/// so files are submitted in batches and a failed batch is retried individually to find the
/// one file responsible.</para>
/// </remarks>
public sealed class CleanerService(
    ICleanTargetProvider targetProvider,
    ScannerOptions? options = null,
    TimeProvider? timeProvider = null) : ICleanerService
{
    private readonly ICleanTargetProvider _targetProvider =
        targetProvider ?? throw new ArgumentNullException(nameof(targetProvider));

    private readonly ScannerOptions _options = options ?? ScannerOptions.Default;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Task<CleanResult> CleanAsync(
        IReadOnlyList<JunkFile> files,
        CleanOptions? options = null,
        IProgress<CleanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        // Resolve the allowlist once per run, not once per file.
        var allowedRoots = _targetProvider.GetTargets().Select(t => t.Path).ToList();
        var effective = options ?? CleanOptions.Default;

        return Task.Run(
            () => CleanCore(files, allowedRoots, effective, progress, cancellationToken),
            cancellationToken);
    }

    private CleanResult CleanCore(
        IReadOnlyList<JunkFile> files,
        IReadOnlyList<string> allowedRoots,
        CleanOptions options,
        IProgress<CleanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        long freedBytes = 0;
        long recycledBytes = 0;
        long attemptedBytes = 0;
        var deleted = 0;
        var recycled = 0;
        var failures = new List<CleanFailure>();

        var cutoffUtc = _options.MinimumAge > TimeSpan.Zero
            ? _time.GetUtcNow().UtcDateTime - _options.MinimumAge
            : DateTime.MinValue;

        var batch = new List<(string Path, long Size)>(Math.Max(1, options.RecycleBatchSize));

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = files[index];
            var path = file.FullPath;
            attemptedBytes += file.SizeBytes;

            if (!TryValidate(path, allowedRoots, cutoffUtc, out var liveSize, out var reason))
            {
                failures.Add(new CleanFailure(path, reason!));
                Report(index + 1, path);
                continue;
            }

            if (options.Mode == DeletionMode.Permanent)
            {
                try
                {
                    File.Delete(path);
                    freedBytes += liveSize;
                    deleted++;
                }
                catch (Exception e) when (IsRecoverable(e))
                {
                    // A locked or protected file is an expected outcome, not a fatal error.
                    failures.Add(new CleanFailure(path, Describe(e)));
                }

                Report(index + 1, path);
                continue;
            }

            batch.Add((path, liveSize));
            if (batch.Count >= options.RecycleBatchSize)
            {
                FlushBatch();
            }

            Report(index + 1, path);
        }

        FlushBatch();
        stopwatch.Stop();

        return new CleanResult
        {
            Mode = options.Mode,
            FreedBytes = freedBytes,
            RecycledBytes = recycledBytes,
            DeletedCount = deleted,
            RecycledCount = recycled,
            Failures = failures,
            Duration = stopwatch.Elapsed,
            AttemptedBytes = attemptedBytes,
        };

        void FlushBatch()
        {
            if (batch.Count == 0)
            {
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                foreach (var item in batch)
                {
                    failures.Add(new CleanFailure(item.Path, "The Recycle Bin is only available on Windows"));
                }

                batch.Clear();
                return;
            }

            // The shell cannot cope with extended-length paths; refuse those explicitly rather
            // than letting it silently mishandle them.
            var usable = new List<(string Path, long Size)>(batch.Count);
            foreach (var item in batch)
            {
                if (RecycleBin.IsSupported(item.Path))
                {
                    usable.Add(item);
                }
                else
                {
                    failures.Add(new CleanFailure(item.Path, "Path too long for the Recycle Bin"));
                }
            }

            if (usable.Count == 0)
            {
                batch.Clear();
                return;
            }

            if (RecycleBin.TryRecycleBatch(usable.Select(u => u.Path).ToList(), out var batchError))
            {
                foreach (var item in usable)
                {
                    recycledBytes += item.Size;
                    recycled++;
                }

                batch.Clear();
                return;
            }

            // The batch failed as a unit. Retry one file at a time so the failure lands on the
            // specific file that caused it instead of tarring every file in the batch with it.
            foreach (var item in usable)
            {
                if (RecycleBin.TryRecycleBatch([item.Path], out var singleError))
                {
                    recycledBytes += item.Size;
                    recycled++;
                }
                else
                {
                    failures.Add(new CleanFailure(item.Path, singleError ?? batchError ?? "Could not recycle"));
                }
            }

            batch.Clear();
        }

        void Report(int processed, string? currentPath)
        {
            progress?.Report(new CleanProgress(
                Processed: processed,
                Total: files.Count,
                Deleted: deleted,
                Recycled: recycled,
                Failed: failures.Count,
                FreedBytes: freedBytes,
                RecycledBytes: recycledBytes,
                CurrentPath: currentPath));
        }
    }

    /// <summary>
    /// The full set of pre-flight checks a file must pass before it is touched: allowlist
    /// membership, reparse-point ancestry, existence, that it is a file, and freshness.
    /// </summary>
    private static bool TryValidate(
        string path,
        IReadOnlyList<string> allowedRoots,
        DateTime cutoffUtc,
        out long liveSize,
        out string? reason)
    {
        liveSize = 0;
        reason = null;

        var rejection = PathGuard.GetRejectionReason(path, allowedRoots);
        if (rejection is not null)
        {
            reason = $"Refused: {rejection}";
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                reason = "No longer exists";
                return false;
            }

            if (info.Attributes.HasFlag(FileAttributes.Directory))
            {
                // The scanner only ever returns files; refuse if one turns up anyway.
                reason = "Refused: not a file";
                return false;
            }

            liveSize = info.Length;

            if (cutoffUtc != DateTime.MinValue && info.LastWriteTimeUtc > cutoffUtc)
            {
                reason = "Modified too recently - skipped to avoid data loss";
                return false;
            }

            // Read-only blocks File.Delete. Clearing it is standard for a temp cleaner; if the
            // attribute cannot be cleared the attempt fails and is reported like any other.
            if (info.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                info.Attributes &= ~FileAttributes.ReadOnly;
            }

            return true;
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            reason = Describe(e);
            return false;
        }
    }

    /// <summary>Exceptions that mean "skip this file", never "abort the clean".</summary>
    private static bool IsRecoverable(Exception e) =>
        e is UnauthorizedAccessException
            or IOException
            or System.Security.SecurityException
            or PathTooLongException
            or NotSupportedException;

    private static string Describe(Exception e) => e switch
    {
        UnauthorizedAccessException => "Access denied (try running as administrator)",
        IOException => "In use by another process",
        _ => e.Message,
    };
}
