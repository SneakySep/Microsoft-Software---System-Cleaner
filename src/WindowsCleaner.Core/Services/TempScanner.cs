using System.Diagnostics;
using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Safety;
using WindowsCleaner.Core.Targets;

namespace WindowsCleaner.Core.Services;

/// <summary>
/// Walks the configured temp locations and reports every disposable file it can safely reach.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> Directory enumeration is synchronous, I/O-bound work. Rather than
/// fake asynchrony over it, the whole walk runs inside a single <see cref="Task.Run{TResult}(Func{TResult})"/>
/// so it never occupies the UI thread, and progress is marshalled back through
/// <see cref="IProgress{T}"/>.</para>
///
/// <para><b>Robustness.</b> A naive <c>Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)</c>
/// aborts the entire walk the moment it meets one inaccessible sub-directory, which in
/// <c>%TEMP%</c> is guaranteed to happen. Instead this uses an explicit stack and wraps every
/// individual file system interaction, so one bad entry costs one entry and the scan continues.</para>
///
/// <para><b>Safety.</b> Reparse points are never followed or yielded, and every path is
/// re-checked against its root before it is reported. See <see cref="PathGuard"/>.</para>
/// </remarks>
public sealed class TempScanner(
    ICleanTargetProvider targetProvider,
    ScannerOptions? options = null,
    TimeProvider? timeProvider = null) : IScannerService
{
    private readonly ICleanTargetProvider _targetProvider =
        targetProvider ?? throw new ArgumentNullException(nameof(targetProvider));

    private readonly ScannerOptions _options = options ?? ScannerOptions.Default;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Task<ScanResult> ScanAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Offload the synchronous walk; ScanCore is entirely self-contained and thread-safe.
        return Task.Run(() => ScanCore(progress, cancellationToken), cancellationToken);
    }

    private ScanResult ScanCore(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var targets = _targetProvider.GetTargets();
        var allowedRoots = targets.Select(t => t.Path).ToList();

        var files = new List<JunkFile>();
        var skipped = new Dictionary<SkipReason, int>();
        var summaries = new List<TargetSummary>();

        // Everything older than this is considered stale. Computed once per scan so the
        // comparison does not drift as the walk progresses.
        var cutoffUtc = _options.MinimumAge > TimeSpan.Zero
            ? _time.GetUtcNow().UtcDateTime - _options.MinimumAge
            : DateTime.MaxValue;

        var lastReportFiles = 0;
        var lastReportAt = Stopwatch.GetTimestamp();
        long bytesFound = 0;

        for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var target = targets[targetIndex];
            Report(target, targetIndex, targets.Count, files.Count, bytesFound);

            var targetFiles = 0;
            long targetBytes = 0;
            string? accessError = null;

            if (!Directory.Exists(target.Path))
            {
                summaries.Add(new TargetSummary
                {
                    DisplayName = target.DisplayName,
                    Path = target.Path,
                    Accessible = false,
                    Error = "Directory does not exist",
                    FileCount = 0,
                    TotalBytes = 0,
                    IsUserAdded = target.IsUserAdded,
                });
                continue;
            }

            var stack = new Stack<(string Directory, int Depth)>();
            stack.Push((target.Path, 0));

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (directory, depth) = stack.Pop();

                // The roots themselves are never reported, and nothing outside them is either.
                if (!directory.Equals(target.Path, StringComparison.OrdinalIgnoreCase)
                    && !PathGuard.IsStrictlyWithin(directory, target.Path))
                {
                    continue;
                }

                IEnumerator<FileSystemInfo> enumerator;
                try
                {
                    enumerator = new DirectoryInfo(directory).EnumerateFileSystemInfos().GetEnumerator();
                }
                catch (Exception e) when (IsRecoverable(e))
                {
                    Bump(skipped, ToSkipReason(e));
                    accessError ??= Describe(e);
                    continue;
                }

                using (enumerator)
                {
                    // EnumerateFileSystemInfos is lazy: failures surface from MoveNext, not from
                    // the call above, so the try/catch has to wrap the iteration itself.
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        FileSystemInfo entry;
                        try
                        {
                            if (!enumerator.MoveNext())
                            {
                                break;
                            }

                            entry = enumerator.Current;
                        }
                        catch (Exception e) when (IsRecoverable(e))
                        {
                            Bump(skipped, ToSkipReason(e));
                            accessError ??= Describe(e);
                            break; // This directory is done; the rest of the scan continues.
                        }

                        var entryPath = entry.FullName;

                        FileAttributes attributes;
                        try
                        {
                            attributes = entry.Attributes;
                        }
                        catch (Exception e) when (IsRecoverable(e))
                        {
                            Bump(skipped, ToSkipReason(e));
                            continue;
                        }

                        // Never descend through, or offer up, a symlink/junction. A junction inside
                        // %TEMP% pointing elsewhere is exactly how a temp cleaner causes data loss.
                        if (PathGuard.IsReparsePoint(attributes))
                        {
                            Bump(skipped, SkipReason.ReparsePoint);
                            continue;
                        }

                        if (attributes.HasFlag(FileAttributes.Directory))
                        {
                            if (depth + 1 < _options.MaxDepth)
                            {
                                stack.Push((entryPath, depth + 1));
                            }
                            else
                            {
                                Bump(skipped, SkipReason.Error);
                            }

                            continue;
                        }

                        // A fresh FileInfo does one stat for authoritative size and timestamp,
                        // rather than trusting values cached during the directory enumeration.
                        long size;
                        DateTime lastWriteUtc;
                        try
                        {
                            var info = new FileInfo(entryPath);
                            if (!info.Exists)
                            {
                                Bump(skipped, SkipReason.NotFound);
                                continue;
                            }

                            lastWriteUtc = info.LastWriteTimeUtc;
                            size = info.Length;
                        }
                        catch (Exception e) when (IsRecoverable(e))
                        {
                            Bump(skipped, ToSkipReason(e));
                            continue;
                        }

                        // Freshness guard: an application may be mid-write on this file right now.
                        if (lastWriteUtc > cutoffUtc)
                        {
                            Bump(skipped, SkipReason.TooRecent);
                            continue;
                        }

                        if (!PathGuard.IsStrictlyWithin(entryPath, target.Path))
                        {
                            Bump(skipped, SkipReason.UnsafePath);
                            continue;
                        }

                        files.Add(new JunkFile
                        {
                            FullPath = entryPath,
                            SizeBytes = size,
                            LastWriteTimeUtc = lastWriteUtc,
                            TargetName = target.DisplayName,
                        });

                        targetFiles++;
                        targetBytes += size;
                        bytesFound += size;

                        if (files.Count >= _options.MaxFiles)
                        {
                            break;
                        }

                        if (ShouldReport(files.Count, ref lastReportFiles, ref lastReportAt))
                        {
                            Report(target, targetIndex, targets.Count, files.Count, bytesFound, entryPath);
                        }
                    }
                }

                if (files.Count >= _options.MaxFiles)
                {
                    break;
                }
            }

            summaries.Add(new TargetSummary
            {
                DisplayName = target.DisplayName,
                Path = target.Path,
                Accessible = accessError is null,
                Error = accessError,
                FileCount = targetFiles,
                TotalBytes = targetBytes,
                IsUserAdded = target.IsUserAdded,
            });

            if (files.Count >= _options.MaxFiles)
            {
                break;
            }
        }

        stopwatch.Stop();

        // Largest first: that ordering is what a user acts on.
        files.Sort(static (a, b) => b.SizeBytes.CompareTo(a.SizeBytes));

        return new ScanResult
        {
            Files = files,
            TotalSizeBytes = files.Sum(static f => f.SizeBytes),
            Duration = stopwatch.Elapsed,
            Targets = summaries,
            Skipped = skipped,
        };

        void Report(
            CleanTarget target,
            int index,
            int count,
            int found,
            long foundBytes,
            string? currentPath = null)
        {
            if (progress is null)
            {
                return;
            }

            progress.Report(new ScanProgress(
                target.DisplayName,
                index,
                count,
                found,
                BytesFound: foundBytes,
                CurrentPath: currentPath));
        }

        bool ShouldReport(int found, ref int lastCount, ref long lastTick)
        {
            var now = Stopwatch.GetTimestamp();
            var byCount = found - lastCount >= _options.ProgressFileInterval;
            var byTime = Stopwatch.GetElapsedTime(lastTick, now) >= _options.ProgressTimeInterval;

            if (!byCount && !byTime)
            {
                return false;
            }

            lastCount = found;
            lastTick = now;
            return true;
        }
    }

    private static void Bump(Dictionary<SkipReason, int> counters, SkipReason reason) =>
        counters[reason] = counters.GetValueOrDefault(reason) + 1;

    /// <summary>Exceptions that mean "skip this entry", never "abort the scan".</summary>
    private static bool IsRecoverable(Exception e) =>
        e is UnauthorizedAccessException
            or IOException
            or System.Security.SecurityException
            or PathTooLongException
            or NotSupportedException;

    private static SkipReason ToSkipReason(Exception e) => e switch
    {
        UnauthorizedAccessException => SkipReason.AccessDenied,
        PathTooLongException => SkipReason.Error,
        IOException => SkipReason.Locked,
        _ => SkipReason.Error,
    };

    private static string Describe(Exception e) => e switch
    {
        UnauthorizedAccessException => "Access denied (try running as administrator)",
        _ => e.Message,
    };
}
