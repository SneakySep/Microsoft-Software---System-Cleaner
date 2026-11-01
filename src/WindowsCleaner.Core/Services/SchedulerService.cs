using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Scheduling;

namespace WindowsCleaner.Core.Services;

/// <inheritdoc cref="ISchedulerService"/>
[SupportedOSPlatform("windows")]
public sealed class SchedulerService : ISchedulerService
{
    /// <summary>
    /// The desktop app's executable name.
    /// </summary>
    /// <remarks>
    /// Refused as a scheduling target. The job runs <c>clean --yes</c>, and the desktop app does
    /// not implement that command line: it would open a window at 3am and clean nothing, forever,
    /// with no indication that anything was wrong. Catching it here turns a silent no-op into an
    /// error message.
    /// </remarks>
    private const string DesktopAppExecutable = "WindowsCleaner.exe";

    /// <summary>Cap on captured output, so a chatty run cannot flood the caller.</summary>
    private const int MaxCapturedOutput = 4000;

    public ScheduledTaskState GetState()
    {
        // The query's own error is deliberately not surfaced: when the scheduler probes healthy,
        // "no task matches the name" is the normal and expected answer.
        if (!ScheduledCleanTask.TryQueryXml(out var xml, out _))
        {
            // A failed query is ambiguous, so ask the scheduler an easier question before
            // concluding that the job is simply not there.
            return ScheduledCleanTask.TryProbeScheduler(out var probeError)
                ? ScheduledTaskState.NotInstalled
                : new ScheduledTaskState
                {
                    IsInstalled = false,
                    Error = $"Task Scheduler could not be queried, so the scheduled job's state is "
                        + $"unknown: {probeError}",
                };
        }

        if (!ScheduledTaskSnapshot.TryParse(xml, out var snapshot, out var parseError))
        {
            return new ScheduledTaskState
            {
                IsInstalled = true,
                Error = parseError,
            };
        }

        return new ScheduledTaskState { IsInstalled = true, Snapshot = snapshot };
    }

    public bool TryLocateCli(out string? path, out IReadOnlyList<string> searched) =>
        WcleanLocator.TryLocate(out path, out searched);

    public ScheduleChangeResult Enable(ScheduledCleanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var executable = options.ExecutablePath;

        if (string.IsNullOrWhiteSpace(executable))
        {
            if (!WcleanLocator.TryLocate(out executable, out var searched))
            {
                return new ScheduleChangeResult(
                    false,
                    $"Could not find {WcleanLocator.ExecutableName}, which is what the scheduled job runs. "
                        + $"Set {WcleanLocator.PathVariable} to its full path, or build the command-line "
                        + $"project. Looked in: {string.Join("; ", searched)}");
            }
        }
        else if (Path.GetFileName(executable).Equals(DesktopAppExecutable, StringComparison.OrdinalIgnoreCase))
        {
            return new ScheduleChangeResult(
                false,
                "That is the desktop app, not the command-line tool. The scheduled job runs "
                    + $"'{WcleanLocator.ExecutableName} clean', which the app would ignore.");
        }

        if (!File.Exists(executable))
        {
            return new ScheduleChangeResult(false, $"That file does not exist: {executable}");
        }

        if (!ScheduledCleanTask.TryRegister(options, executable, out var message))
        {
            return new ScheduleChangeResult(false, message);
        }

        return new ScheduleChangeResult(
            true,
            $"Scheduled: {ScheduledCleanTask.Describe(options, executable)}");
    }

    public ScheduleChangeResult Disable()
    {
        // Treating an absent job as a successful removal keeps the button idempotent: pressing it
        // when the job is already gone should not look like a failure.
        if (!ScheduledCleanTask.TryQuery(out _, out _)
            && ScheduledCleanTask.TryProbeScheduler(out _))
        {
            return new ScheduleChangeResult(true, "There was no scheduled job to remove.");
        }

        return ScheduledCleanTask.TryRemove(out var message)
            ? new ScheduleChangeResult(true, $"Removed '{ScheduledCleanTask.TaskName}'.")
            : new ScheduleChangeResult(false, message);
    }

    public async Task<ScheduledRunResult> RunNowAsync(CancellationToken cancellationToken = default)
    {
        if (!ScheduledCleanTask.TryQueryXml(out var xml, out _)
            || !ScheduledTaskSnapshot.TryParse(xml, out var snapshot, out _)
            || snapshot is null)
        {
            return new ScheduledRunResult(-1, string.Empty, false, "There is no scheduled job to run.");
        }

        // Checked before starting so a job pointing at a moved or deleted program reports that,
        // rather than an error from the process layer quoting a path the user never chose.
        if (!File.Exists(snapshot.ExecutablePath))
        {
            return new ScheduledRunResult(
                -1,
                string.Empty,
                false,
                $"The program this job runs is missing: {snapshot.ExecutablePath}");
        }

        return await RunAsync(snapshot, cancellationToken);
    }

    /// <summary>Runs the job's own command and waits for it, capturing what it printed.</summary>
    private static async Task<ScheduledRunResult> RunAsync(
        ScheduledTaskSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(snapshot.ExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // The definition stores its arguments as one string, so they are split properly rather
        // than handed over whole: a quoted path containing a space would otherwise arrive as
        // several arguments.
        foreach (var argument in ScheduledTaskSnapshot.SplitArguments(snapshot.Arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return new ScheduledRunResult(-1, string.Empty, false, e.Message);
        }

        // Both streams are drained while the process runs. Waiting first would deadlock a run
        // whose output filled the pipe buffer.
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            // The reads complete once the killed process's streams close.
            var partial = await SafeRead(standardOutput);
            await SafeRead(standardError);

            return new ScheduledRunResult(-1, partial, true);
        }

        return new ScheduledRunResult(
            process.ExitCode,
            Capture(await SafeRead(standardOutput), await SafeRead(standardError)),
            false);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // The whole tree: a stopped job must not leave a child behind still deleting.
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    private static async Task<string> SafeRead(Task<string> read)
    {
        try
        {
            return await read;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // The stream was cut short, which is what killing a running process does.
            return string.Empty;
        }
    }

    private static string Capture(string standardOutput, string standardError)
    {
        var text = standardOutput.Trim();

        // Falls back to stderr, where a failure to start would have been reported.
        if (text.Length == 0)
        {
            text = standardError.Trim();
        }

        return text.Length <= MaxCapturedOutput ? text : text[..MaxCapturedOutput];
    }
}
