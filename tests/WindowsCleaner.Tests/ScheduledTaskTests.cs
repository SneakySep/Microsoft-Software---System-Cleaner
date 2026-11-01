using System.Runtime.Versioning;
using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Scheduling;

namespace WindowsCleaner.Tests;

/// <summary>
/// Argument construction for the scheduled cleaning job.
/// </summary>
/// <remarks>
/// Only the generated command is asserted here. Actually registering a job would modify the
/// user's Task Scheduler, which a unit test has no business doing; the real create/query/delete
/// round trip is exercised during verification instead.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ScheduledTaskTests
{
    private const string Exe = @"C:\Tools\wclean.exe";

    [Fact]
    public void Builds_a_daily_job_at_the_requested_time()
    {
        var options = new ScheduledCleanOptions { TimeOfDay = new TimeSpan(3, 30, 0) };

        var arguments = ScheduledCleanTask.BuildCreateArguments(options, Exe);

        Assert.Contains("/Create", arguments);
        Assert.Contains(ScheduledCleanTask.TaskName, arguments);
        Assert.Contains("DAILY", arguments);
        Assert.Contains("03:30", arguments);

        // No weekday is supplied for a daily job.
        Assert.DoesNotContain("/D", arguments);
    }

    [Fact]
    public void Builds_a_weekly_job_with_the_three_letter_weekday()
    {
        var options = new ScheduledCleanOptions
        {
            Frequency = ScheduleFrequency.Weekly,
            Day = DayOfWeek.Wednesday,
            TimeOfDay = new TimeSpan(22, 5, 0),
        };

        var arguments = ScheduledCleanTask.BuildCreateArguments(options, Exe);

        Assert.Contains("WEEKLY", arguments);
        Assert.Contains("WED", arguments);
        Assert.Contains("22:05", arguments);
    }

    [Fact]
    public void Registers_the_job_with_the_least_privileged_token()
    {
        var arguments = ScheduledCleanTask.BuildCreateArguments(new ScheduledCleanOptions(), Exe);

        var index = Array.IndexOf(arguments, "/RL");
        Assert.True(index >= 0, "expected an explicit run level");
        Assert.Equal("LIMITED", arguments[index + 1]);

        // /F makes re-running the install command idempotent rather than an error.
        Assert.Contains("/F", arguments);
    }

    [Fact]
    public void Overwrites_an_existing_job_so_installing_twice_is_safe()
    {
        var arguments = ScheduledCleanTask.BuildCreateArguments(new ScheduledCleanOptions(), Exe);

        Assert.Contains("/F", arguments);
    }

    /// <summary>
    /// The scheduled command must be non-interactive, or the job would hang forever waiting on a
    /// prompt that nobody can answer.
    /// </summary>
    [Fact]
    public void The_scheduled_command_is_unattended_and_quoted()
    {
        var options = new ScheduledCleanOptions
        {
            Mode = DeletionMode.Permanent,
            MinimumAge = TimeSpan.FromHours(24),
        };

        var command = ScheduledCleanTask.BuildTaskCommand(options, @"C:\Program Files\Windows Cleaner\wclean.exe");

        Assert.Contains("--yes", command);
        Assert.Contains("clean", command);
        Assert.Contains("--mode permanent", command);
        Assert.Contains("--min-age 1440", command);

        // The run labels itself as scheduled. It has to be stamped on at registration: nothing
        // about the finished run would afterwards tell it apart from one somebody typed, and that
        // distinction is what lets the app report when the job last ran.
        Assert.Contains($"--origin {CleanReportSources.Scheduled}", command);

        // The path is quoted so the space in "Program Files" survives being passed to schtasks.
        Assert.StartsWith("\"C:\\Program Files\\Windows Cleaner\\wclean.exe\"", command);
    }

    [Fact]
    public void Scheduled_runs_default_to_permanent_deletion()
    {
        // Recycling on a repeating job would just fill the Recycle Bin and never reclaim space.
        var options = new ScheduledCleanOptions();

        Assert.Equal(DeletionMode.Permanent, options.Mode);

        // ...and the longer freshness guard is what keeps that safe.
        Assert.Equal(TimeSpan.FromHours(24), options.MinimumAge);
        Assert.Contains("--min-age 1440", ScheduledCleanTask.BuildTaskCommand(options, Exe));
    }

    [Fact]
    public void Honours_a_recycle_mode_override()
    {
        var options = new ScheduledCleanOptions { Mode = DeletionMode.RecycleBin };

        Assert.Contains("--mode recycle", ScheduledCleanTask.BuildTaskCommand(options, Exe));
    }

    [Fact]
    public void Builds_delete_and_query_arguments_for_the_same_task()
    {
        Assert.Contains(ScheduledCleanTask.TaskName, ScheduledCleanTask.BuildDeleteArguments());
        Assert.Contains("/Delete", ScheduledCleanTask.BuildDeleteArguments());
        Assert.Contains(ScheduledCleanTask.TaskName, ScheduledCleanTask.BuildQueryArguments());
    }

    [Fact]
    public void Describes_the_schedule_in_words()
    {
        var weekly = new ScheduledCleanOptions
        {
            Frequency = ScheduleFrequency.Weekly,
            Day = DayOfWeek.Monday,
            TimeOfDay = new TimeSpan(1, 0, 0),
        };

        var description = ScheduledCleanTask.Describe(weekly, Exe);

        Assert.Contains("every Monday", description);
        Assert.Contains("01:00", description);
    }

    [Fact]
    public void Task_name_is_namespaced_out_of_the_scheduler_root()
    {
        Assert.StartsWith(@"\", ScheduledCleanTask.TaskName);
        Assert.Contains("WindowsCleaner", ScheduledCleanTask.TaskName);
    }
}
