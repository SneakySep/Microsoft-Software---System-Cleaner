using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using WindowsCleaner.Core.Models;

namespace WindowsCleaner.Core.Scheduling;

/// <summary>
/// Registers, inspects and removes a Windows Task Scheduler job that runs the CLI's
/// <c>clean</c> command unattended.
/// </summary>
/// <remarks>
/// <para>
/// Driving the platform's own scheduler rather than running a timer inside the app means the
/// cleaning still happens when nothing is running, which is the whole point of scheduling it.
/// The job simply invokes the existing <c>wclean clean</c> command, so a scheduled run goes
/// through exactly the same scanning, allowlist and safety code as a manual one.
/// </para>
/// <para>
/// Implemented over <c>schtasks.exe</c> instead of the Task Scheduler COM API. <c>schtasks</c>
/// is present on every supported Windows version, and the alternative would mean hand-written
/// vtable-accurate COM interop for a job that only ever needs to be created, queried and
/// deleted.
/// </para>
/// <para>
/// The task is registered with <c>/RL LIMITED</c>, so it runs with the invoking user's normal
/// token rather than elevated privileges.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ScheduledCleanTask
{
    private const string SchTasks = "schtasks.exe";

    /// <summary>Fully-qualified task path, keeping our job out of the scheduler's root.</summary>
    public const string TaskName = @"\WindowsCleaner\ScheduledClean";

    /// <summary>Builds the argument list for creating the job. Separated out so it can be asserted.</summary>
    public static string[] BuildCreateArguments(ScheduledCleanOptions options, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var arguments = new List<string>
        {
            "/Create",
            "/TN", TaskName,
            "/TR", BuildTaskCommand(options, executablePath),
            "/SC", options.Frequency == ScheduleFrequency.Weekly ? "WEEKLY" : "DAILY",
        };

        if (options.Frequency == ScheduleFrequency.Weekly)
        {
            arguments.Add("/D");
            arguments.Add(ToSchedulerDay(options.Day));
        }

        arguments.Add("/ST");
        arguments.Add(ScheduleText.FormatTimeOfDay(options.TimeOfDay));

        // /F overwrites an existing job, making re-running the command idempotent.
        arguments.Add("/F");
        arguments.Add("/RL");
        arguments.Add("LIMITED");

        return [.. arguments];
    }

    public static string[] BuildDeleteArguments() => ["/Delete", "/TN", TaskName, "/F"];

    public static string[] BuildQueryArguments() => ["/Query", "/TN", TaskName, "/FO", "LIST"];

    /// <summary>
    /// Asks for the task definition as XML.
    /// </summary>
    /// <remarks>
    /// Used instead of the LIST output because the XML schema's element names are the same on
    /// every display language, whereas the LIST labels are translated.
    /// </remarks>
    public static string[] BuildQueryXmlArguments() => ["/Query", "/TN", TaskName, "/XML"];

    /// <summary>The command line the scheduler will run, quoted as schtasks expects.</summary>
    public static string BuildTaskCommand(ScheduledCleanOptions options, string executablePath)
    {
        var mode = options.Mode == DeletionMode.RecycleBin ? "recycle" : "permanent";
        var minutes = (int)Math.Round(options.MinimumAge.TotalMinutes);

        // --yes is required: a scheduled run has no console to confirm on, and the CLI refuses
        // to delete without it precisely so this cannot happen by accident.
        //
        // --origin labels the run as scheduled, so the history can tell an unattended clean apart
        // from one somebody typed. It has to be recorded when the job is registered: afterwards
        // there is nothing left to distinguish them by.
        return $"\"{executablePath}\" clean --yes --mode {mode} --min-age {minutes} "
            + $"--origin {CleanReportSources.Scheduled}";
    }

    public static bool TryRegister(ScheduledCleanOptions options, out string message) =>
        TryRegister(options, options.ExecutablePath ?? Environment.ProcessPath, out message);

    /// <summary>Registers the job, running a specific executable.</summary>
    /// <remarks>
    /// The explicit path matters: the CLI schedules itself, whereas the desktop app has to
    /// schedule <c>wclean.exe</c>, which is a different file resolved by
    /// <see cref="WcleanLocator"/>. Defaulting to the calling process here would quietly
    /// register a job that runs the wrong binary.
    /// </remarks>
    public static bool TryRegister(ScheduledCleanOptions options, string? executablePath, out string message)
    {
        ArgumentNullException.ThrowIfNull(options);

        var executable = executablePath;

        if (string.IsNullOrWhiteSpace(executable))
        {
            message = "Could not determine the executable to schedule.";
            return false;
        }

        if (!File.Exists(executable))
        {
            message = $"The executable to schedule does not exist: {executable}";
            return false;
        }

        return Run(BuildCreateArguments(options, executable), out message);
    }

    public static bool TryRemove(out string message) => Run(BuildDeleteArguments(), out message);

    /// <summary>True when the job exists; <paramref name="details"/> carries the scheduler's report.</summary>
    public static bool TryQuery(out string details, out string error) =>
        Run(BuildQueryArguments(), out details, out error);

    /// <summary>
    /// Raw task definition XML, or a descriptive error when the task is not there.
    /// </summary>
    /// <remarks>
    /// The raw form is returned rather than a parsed snapshot because the two failure modes have
    /// to stay distinguishable: a job that is not registered and a job whose definition cannot be
    /// read are different answers to "is this thing scheduled?", and only the caller knows which
    /// it is reporting.
    /// </remarks>
    public static bool TryQueryXml(out string xml, out string error) =>
        Run(BuildQueryXmlArguments(), out xml, out error);

    /// <summary>
    /// Checks that the scheduler itself answers at all.
    /// </summary>
    /// <remarks>
    /// Used only after a query for our own task has failed, to tell "this job is not registered"
    /// apart from "Task Scheduler is unavailable" (disabled by policy, for example). The two
    /// look identical from a single failed query, and reporting the second as simply "not
    /// scheduled" would tell the user their job is gone when in fact it may still be running.
    /// </remarks>
    public static bool TryProbeScheduler(out string error) =>
        Run(["/Query", "/FO", "LIST"], out _, out error);

    /// <summary>Renders an options set the way the scheduler will interpret it, for display.</summary>
    public static string Describe(ScheduledCleanOptions options, string executablePath)
    {
        var when = options.Frequency == ScheduleFrequency.Weekly
            ? $"every {options.Day} at {ScheduleText.FormatTimeOfDay(options.TimeOfDay)}"
            : $"daily at {ScheduleText.FormatTimeOfDay(options.TimeOfDay)}";

        return $"runs {when}: {BuildTaskCommand(options, executablePath)}";
    }

    private static string ToSchedulerDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "MON",
        DayOfWeek.Tuesday => "TUE",
        DayOfWeek.Wednesday => "WED",
        DayOfWeek.Thursday => "THU",
        DayOfWeek.Friday => "FRI",
        DayOfWeek.Saturday => "SAT",
        _ => "SUN",
    };

    private static bool Run(IReadOnlyList<string> arguments, out string message) =>
        Run(arguments, out message, out _);

    private static bool Run(IReadOnlyList<string> arguments, out string output, out string error)
    {
        error = string.Empty;
        output = string.Empty;

        try
        {
            // ArgumentList rather than a single string: it handles the nested quoting that the
            // /TR value needs, where the executable path and its arguments must survive as one
            // argument containing embedded quotes.
            var startInfo = new ProcessStartInfo(SchTasks)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                error = "Could not start schtasks.exe.";
                output = error;
                return false;
            }

            // Read both streams before waiting, so a full pipe buffer cannot deadlock.
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            output = string.IsNullOrWhiteSpace(standardOutput) ? standardError : standardOutput;

            if (process.ExitCode != 0)
            {
                error = BuildError(standardError, standardOutput, process.ExitCode);
                return false;
            }

            return true;
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            error = e.Message;
            output = e.Message;
            return false;
        }
    }

    private static string BuildError(string standardError, string standardOutput, int exitCode)
    {
        var detail = !string.IsNullOrWhiteSpace(standardError) ? standardError : standardOutput;
        detail = detail.Trim();

        return string.IsNullOrEmpty(detail)
            ? $"schtasks.exe failed with exit code {exitCode}."
            : detail;
    }
}
