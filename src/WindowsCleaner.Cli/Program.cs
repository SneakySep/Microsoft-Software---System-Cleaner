using System.Runtime.Versioning;
using System.Text.Json;
using WindowsCleaner.Core.Configuration;
using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Scheduling;
using WindowsCleaner.Core.Services;
using WindowsCleaner.Core.Targets;
using WindowsCleaner.Core.Utilities;

namespace WindowsCleaner.Cli;

/// <summary>
/// Headless front-end over the same engine the desktop app uses.
/// </summary>
/// <remarks>
/// The scanning and deletion logic lives entirely in <c>WindowsCleaner.Core</c>; this project
/// only parses arguments, decides whether to ask for confirmation, and prints results.
/// </remarks>
internal static class Program
{
    private const int InteractiveDefaultMinimumAgeMinutes = 15;
    private const int ScheduledDefaultMinimumAgeMinutes = 24 * 60;

    private static async Task<int> Main(string[] args)
    {
        var options = CommandLine.Parse(args, out var error);

        if (options is null)
        {
            Console.Error.WriteLine($"error: {error}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(CommandLine.Usage);
            return ExitCodes.Usage;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(CommandLine.Usage);
            return ExitCodes.Success;
        }

        try
        {
            return options.Command switch
            {
                CliCommand.Schedule => RunSchedule(options),
                CliCommand.Scan => await RunScanAsync(options),
                CliCommand.Clean => await RunCleanAsync(options),
                _ => ExitCodes.Usage,
            };
        }
        catch (Exception ex)
        {
            // Nothing below this point should throw for ordinary file system conditions, so a
            // failure here is genuinely unexpected and deserves a non-zero exit.
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    // =============================== scan / clean ===============================

    private static async Task<int> RunScanAsync(CliOptions options)
    {
        var (provider, composite) = BuildTargetProvider(options);
        var scanner = new TempScanner(provider, BuildScannerOptions(options));

        using var progress = new ConsoleProgress(options.Json);
        var result = await scanner.ScanAsync(progress);

        ReportRejectedFolders(composite);
        Output.WriteScanReport(result, options);

        return ExitCodes.Success;
    }

    private static async Task<int> RunCleanAsync(CliOptions options)
    {
        var (provider, composite) = BuildTargetProvider(options);
        var scannerOptions = BuildScannerOptions(options);
        var scanner = new TempScanner(provider, scannerOptions);
        var cleaner = new CleanerService(provider, scannerOptions);

        using var scanProgress = new ConsoleProgress(options.Json);
        var scan = await scanner.ScanAsync(scanProgress);

        ReportRejectedFolders(composite);
        Output.WriteScanReport(scan, options);

        if (scan.Files.Count == 0)
        {
            Console.WriteLine("Nothing to clean.");
            return ExitCodes.Success;
        }

        if (!Confirm(
                options,
                options.Mode ?? DeletionMode.RecycleBin,
                scan.Files.Count,
                "clean"))
        {
            Console.Error.WriteLine("Aborted; nothing was removed.");
            return ExitCodes.Declined;
        }

        // Read the bin around the clean so the recorded report can distinguish space that came
        // back from space that merely moved into the Recycle Bin.
        var binBefore = QueryRecycleBin();

        using var cleanProgress = new ConsoleProgress(options.Json);
        var clean = await cleaner.CleanAsync(
            scan.Files,
            new CleanOptions { Mode = options.Mode ?? DeletionMode.RecycleBin },
            cleanProgress);

        Output.WriteCleanReport(clean, options);
        RecordCleanReport(clean, scan.Files.Count, binBefore, QueryRecycleBin(), options.Origin);

        if (clean.FailedCount > 0 && !options.Json)
        {
            Console.WriteLine("  Files that could not be removed:");
            Output.WriteFailureSummary(clean);
            Console.WriteLine();
        }

        // Only a total failure is an error; a partial clean is a normal outcome in a live temp folder.
        return clean.RemovedCount == 0 ? ExitCodes.Failure : ExitCodes.Success;
    }

    /// <summary>
    /// Records what the clean did, so the desktop app can report it later.
    /// </summary>
    /// <remarks>
    /// Most valuable for a scheduled run: it happens unattended, so the record is the only way
    /// anyone finds out what it removed. Failing to write is deliberately silent, since it is a
    /// convenience for the UI rather than part of the clean's outcome.
    /// </remarks>
    private static void RecordCleanReport(
        CleanResult result,
        int attemptedCount,
        RecycleBinStats binBefore,
        RecycleBinStats binAfter,
        string? origin)
    {
        var report = CleanReport.From(
            result,
            attemptedCount,
            binBefore,
            binAfter,
            DateTimeOffset.UtcNow,
            origin ?? CleanReportSources.Cli);

        new CleanReportStore().Append(report);
    }

    /// <summary>The Recycle Bin's totals, or zeroes where the platform will not answer.</summary>
    private static RecycleBinStats QueryRecycleBin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return RecycleBinStats.Empty;
        }

        try
        {
            return new RecycleBinService().Query();
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
        {
            return RecycleBinStats.Empty;
        }
    }

    private static ScannerOptions BuildScannerOptions(CliOptions options) => new()
    {
        MinimumAge = options.MinimumAge ?? TimeSpan.FromMinutes(InteractiveDefaultMinimumAgeMinutes),
    };

    /// <summary>
    /// Builds the target set: explicit <c>--target</c> directories if given, otherwise the
    /// standard locations augmented with whatever folders the user has saved.
    /// </summary>
    private static (ICleanTargetProvider Provider, CompositeTargetProvider? Composite) BuildTargetProvider(
        CliOptions options)
    {
        if (options.Targets.Count > 0)
        {
            // An explicit list replaces everything, including saved folders, so a one-off run
            // is never widened by state left behind in a settings file.
            var noun = options.Targets.Count == 1 ? "directory" : "directories";
            Console.Error.WriteLine(
                $"note: cleaning {options.Targets.Count} explicitly supplied {noun} instead of the saved locations.");

            return (new StaticTargetProvider(options.Targets), null);
        }

        var settings = new SettingsStore().Load();
        var composite = new CompositeTargetProvider(new DefaultCleanTargetProvider(), settings.ExtraFolders);

        return (composite, composite);
    }

    private static void ReportRejectedFolders(CompositeTargetProvider? composite)
    {
        if (composite is null)
        {
            return;
        }

        foreach (var (path, reason) in composite.RejectedFolders)
        {
            Console.Error.WriteLine($"warning: ignoring saved folder '{path}': {reason}");
        }
    }

    // ================================= schedule =================================

    private static int RunSchedule(CliOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("error: the 'schedule' command requires Windows.");
            return ExitCodes.Failure;
        }

        if (options.ScheduleRemove)
        {
            return RemoveSchedule(options);
        }

        return options.ScheduleInstall
            ? InstallSchedule(options)
            : ShowScheduleStatus(options);
    }

    [SupportedOSPlatform("windows")]
    private static int InstallSchedule(CliOptions options)
    {
        var scheduled = new ScheduledCleanOptions
        {
            Frequency = options.ScheduleFrequency,
            Day = options.ScheduleDay,
            TimeOfDay = options.ScheduleTime,

            // Opposite defaults to an interactive clean, for reasons documented on
            // ScheduledCleanOptions: an unattended, repeating job must actually reclaim space,
            // and safety comes from the much longer freshness guard instead.
            Mode = options.Mode ?? DeletionMode.Permanent,
            MinimumAge = options.MinimumAge ?? TimeSpan.FromMinutes(ScheduledDefaultMinimumAgeMinutes),
        };

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            Console.Error.WriteLine("error: could not determine which executable to schedule.");
            return ExitCodes.Failure;
        }

        Console.WriteLine();
        Console.WriteLine("  Registering a Windows Task Scheduler job:");
        Console.WriteLine($"    task    : {ScheduledCleanTask.TaskName}");
        Console.WriteLine($"    command : {ScheduledCleanTask.BuildTaskCommand(scheduled, executable)}");
        Console.WriteLine($"    schedule: {(scheduled.Frequency == ScheduleFrequency.Weekly ? $"weekly on {scheduled.Day}" : "daily")}" +
                          $" at {scheduled.TimeOfDay:hh\\:mm}");
        Console.WriteLine();

        if (!ConfirmScheduledChange(options, "Create or update this job?"))
        {
            Console.Error.WriteLine("Aborted; the scheduled job was not changed.");
            return ExitCodes.Declined;
        }

        if (!ScheduledCleanTask.TryRegister(scheduled, out var message))
        {
            Console.Error.WriteLine($"error: {message}");
            return ExitCodes.Failure;
        }

        Console.WriteLine($"  Registered '{ScheduledCleanTask.TaskName}'.");
        Console.WriteLine($"  {message.Trim().Replace("\r\n", "\n  ")}");
        Console.WriteLine();
        Console.WriteLine("  Saved folders are picked up automatically, because the job runs 'wclean clean'.");
        Console.WriteLine();

        return ExitCodes.Success;
    }

    [SupportedOSPlatform("windows")]
    private static int RemoveSchedule(CliOptions options)
    {
        if (!ScheduledCleanTask.TryQuery(out _, out _))
        {
            Console.WriteLine("  No scheduled job to remove.");
            return ExitCodes.Success;
        }

        if (!ConfirmScheduledChange(options, $"Delete the scheduled job '{ScheduledCleanTask.TaskName}'?"))
        {
            Console.Error.WriteLine("Aborted; the scheduled job was not changed.");
            return ExitCodes.Declined;
        }

        if (!ScheduledCleanTask.TryRemove(out var message))
        {
            Console.Error.WriteLine($"error: {message}");
            return ExitCodes.Failure;
        }

        Console.WriteLine($"  Removed '{ScheduledCleanTask.TaskName}'.");
        return ExitCodes.Success;
    }

    [SupportedOSPlatform("windows")]
    private static int ShowScheduleStatus(CliOptions options)
    {
        var installed = ScheduledCleanTask.TryQuery(out var details, out _);

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new { taskName = ScheduledCleanTask.TaskName, installed, details = details.Trim() },
                new JsonSerializerOptions { WriteIndented = true }));
            return ExitCodes.Success;
        }

        Console.WriteLine();

        if (!installed)
        {
            Console.WriteLine($"  No scheduled job named '{ScheduledCleanTask.TaskName}'.");
            Console.WriteLine();
            Console.WriteLine("  Create one with, for example:");
            Console.WriteLine("    wclean schedule --install --at 03:00 --yes");
        }
        else
        {
            Console.WriteLine($"  {ScheduledCleanTask.TaskName}");
            Console.WriteLine();
            Console.WriteLine("  " + details.Trim().Replace("\r\n", "\n  "));
        }

        Console.WriteLine();
        return ExitCodes.Success;
    }

    private static bool ConfirmScheduledChange(CliOptions options, string question)
    {
        if (options.Yes)
        {
            return true;
        }

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("error: this needs --yes when input is not interactive.");
            return false;
        }

        Console.Write($"  {question} [y/N] ");
        return IsAffirmative(Console.ReadLine());
    }

    private static bool Confirm(CliOptions options, DeletionMode mode, int fileCount, string verb)
    {
        if (options.Yes)
        {
            return true;
        }

        // Without a terminal there is nobody to ask, and defaulting to "yes" would be the worst
        // possible failure mode for a destructive command.
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine($"error: '{verb}' needs --yes when input is not interactive.");
            Console.Error.WriteLine("       Run 'wclean scan' first to review, then re-run with --yes.");
            return false;
        }

        var action = mode == DeletionMode.RecycleBin ? "Move to the Recycle Bin" : "PERMANENTLY DELETE";
        Console.Write($"  {action} {fileCount:N0} files? [y/N] ");

        return IsAffirmative(Console.ReadLine());
    }

    private static bool IsAffirmative(string? answer) =>
        answer is not null
        && (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
            || answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes a single self-updating status line while work is in flight.
    /// </summary>
    /// <remarks>
    /// Suppressed when output is redirected or JSON was requested, so piped output stays clean
    /// and parseable rather than being polluted with carriage returns.
    /// </remarks>
    private sealed class ConsoleProgress(bool json) : IProgress<ScanProgress>, IProgress<CleanProgress>, IDisposable
    {
        private readonly bool _interactive = !json && !Console.IsOutputRedirected;

        public void Report(ScanProgress value)
        {
            if (!_interactive)
            {
                return;
            }

            Write($"  scanning {value.TargetName}: {value.FilesFound:N0} files, "
                + ByteSizeFormatter.Format(value.BytesFound));
        }

        void IProgress<CleanProgress>.Report(CleanProgress value)
        {
            if (!_interactive)
            {
                return;
            }

            var moved = ByteSizeFormatter.Format(value.RecycledBytes + value.FreedBytes);
            Write($"  removing {value.Processed:N0} / {value.Total:N0}  ({moved})");
        }

        public void Dispose()
        {
            if (_interactive)
            {
                // Wipe the status line so the report starts on a clean row.
                Console.Write('\r' + new string(' ', 78) + '\r');
            }
        }

        private static void Write(string line)
        {
            Console.Write('\r' + line.PadRight(78)[..78]);
            Console.Out.Flush();
        }
    }
}
