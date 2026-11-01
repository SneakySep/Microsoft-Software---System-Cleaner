using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Scheduling;

namespace WindowsCleaner.Core.Services;

/// <summary>
/// The state of the unattended cleaning job, as the scheduler currently holds it.
/// </summary>
public sealed record ScheduledTaskState
{
    public required bool IsInstalled { get; init; }

    /// <summary>What is registered, when it could be read back.</summary>
    public ScheduledTaskSnapshot? Snapshot { get; init; }

    /// <summary>
    /// Set when the scheduler could not be consulted, or answered in a form we could not read.
    /// </summary>
    /// <remarks>
    /// Distinguishes "no job is registered" from "we could not find out", which otherwise look
    /// identical. The second should never be presented as the first: a user told their job is
    /// gone may re-create it, or worse, assume nothing is running.
    /// </remarks>
    public string? Error { get; init; }

    public static ScheduledTaskState NotInstalled { get; } = new() { IsInstalled = false };
}

/// <summary>The outcome of an attempt to change the scheduled job.</summary>
public sealed record ScheduleChangeResult(bool Succeeded, string Message);

/// <summary>The outcome of running the scheduled job on demand.</summary>
public sealed record ScheduledRunResult(int ExitCode, string Output, bool Cancelled, string? StartError = null)
{
    public bool Succeeded => !Cancelled && StartError is null && ExitCode == 0;
}

/// <summary>
/// Manages the scheduled cleaning job without the caller needing to know that
/// <c>schtasks.exe</c> exists.
/// </summary>
/// <remarks>
/// An interface for the same reason <see cref="IRecycleBinService"/> is one: the UI code stays
/// free of platform plumbing, and the decision logic around it can be exercised without
/// touching the real Task Scheduler.
/// </remarks>
public interface ISchedulerService
{
    /// <summary>Reads the current job back from the scheduler.</summary>
    ScheduledTaskState GetState();

    /// <summary>
    /// Finds the command-line front-end that the job should run. Returns false with the list of
    /// places that were checked.
    /// </summary>
    bool TryLocateCli(out string? path, out IReadOnlyList<string> searched);

    /// <summary>
    /// Creates or updates the job. When <see cref="ScheduledCleanOptions.ExecutablePath"/> is
    /// not set, the CLI is located automatically and the change fails with an explanation if it
    /// cannot be found.
    /// </summary>
    ScheduleChangeResult Enable(ScheduledCleanOptions options);

    /// <summary>Removes the job. Removing a job that is not there succeeds.</summary>
    ScheduleChangeResult Disable();

    /// <summary>
    /// Runs the registered job's command now and waits for it.
    /// </summary>
    /// <remarks>
    /// What is registered is what runs, not whatever a dialog happens to be showing: asking to run
    /// the job now should mean the job. Cancelling stops the run in progress, which means files it
    /// has already removed stay removed - the same bargain the dashboard's Cancel makes.
    /// </remarks>
    Task<ScheduledRunResult> RunNowAsync(CancellationToken cancellationToken = default);
}
