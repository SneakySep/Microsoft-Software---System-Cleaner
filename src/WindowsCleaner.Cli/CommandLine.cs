using System.Globalization;
using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Scheduling;

namespace WindowsCleaner.Cli;

internal enum CliCommand
{
    None,
    Scan,
    Clean,
    Schedule,
}

/// <summary>Parsed command-line options.</summary>
internal sealed class CliOptions
{
    public CliCommand Command { get; set; }

    /// <summary>Explicit directories. When non-empty these replace the saved locations.</summary>
    public List<string> Targets { get; } = [];

    /// <summary>
    /// Null means "not specified", so each command can apply its own default. Scheduling
    /// defaults to a 24-hour guard while interactive cleaning uses 15 minutes.
    /// </summary>
    public TimeSpan? MinimumAge { get; set; }

    /// <summary>Null means "not specified"; scheduling defaults to permanent, cleaning to recycle.</summary>
    public DeletionMode? Mode { get; set; }

    public bool Json { get; set; }

    public bool Yes { get; set; }

    /// <summary>
    /// Label recorded against the run in the history. Null means "an ordinary command-line run".
    /// </summary>
    /// <remarks>
    /// The scheduled job passes this so an unattended clean is distinguishable afterwards from
    /// one somebody typed. Nothing about the finished run would otherwise tell the two apart.
    /// </remarks>
    public string? Origin { get; set; }

    public int Top { get; set; } = 20;

    public bool ShowHelp { get; set; }

    // --- schedule ---

    public bool ScheduleInstall { get; set; }

    public bool ScheduleRemove { get; set; }

    public TimeSpan ScheduleTime { get; set; } = new(3, 0, 0);

    public ScheduleFrequency ScheduleFrequency { get; set; } = ScheduleFrequency.Daily;

    public DayOfWeek ScheduleDay { get; set; } = DayOfWeek.Sunday;
}

internal static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int Declined = 3;
}

internal static class CommandLine
{
    public const string Usage = """
        wclean - Windows Cleaner command line

        USAGE
          wclean scan     [options]   Report what could be cleaned
          wclean clean    [options]   Remove what the scan finds
          wclean schedule [options]   Manage the unattended cleaning job

        SCAN / CLEAN OPTIONS
          --target <dir>     Clean this directory instead of the saved locations.
                             Repeatable. Replaces the default set entirely.
          --min-age <mins>   Skip files modified more recently than this (default 15).
                             Use 0 to disable the freshness guard.
          --mode <mode>      How files are disposed of:
                               recycle    move to the Recycle Bin so it can be undone (default)
                               permanent  delete immediately, reclaiming the space now
          --origin <label>   Record the run in the history under this label. The scheduled job
                             uses 'scheduled'; a plain run is recorded as 'cli'.
          --top <n>          How many of the largest files to list (default 20, 0 for none).
          --json             Machine-readable output.
          --yes, -y          Skip the confirmation prompt. Required when not interactive.

        SCHEDULE OPTIONS
          --status           Report the current job. This is the default with no action given.
          --install          Create or update the job.
          --remove           Delete the job.
          --at <HH:mm>       Time of day to run (default 03:00).
          --daily            Run every day (default).
          --weekly           Run once a week.
          --day <day>        Day of week for --weekly, e.g. SUN (default SUN).
          --min-age <mins>   Freshness guard for the job (default 1440, i.e. 24 hours).
          --mode <mode>      Disposal mode for the job (default permanent).

        EXIT CODES
          0  success
          1  the scan or clean failed
          2  bad usage
          3  declined at the confirmation prompt
        """;

    /// <summary>Parses arguments, or returns null with <paramref name="error"/> set.</summary>
    public static CliOptions? Parse(string[] args, out string? error)
    {
        error = null;
        var options = new CliOptions();

        if (args.Length == 0)
        {
            options.ShowHelp = true;
            return options;
        }

        var positional = new List<string>();

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            // Support both "--mode permanent" and "--mode=permanent".
            var separator = argument.IndexOf('=');
            var name = separator >= 0 ? argument[..separator] : argument;

            switch (name)
            {
                case "-h" or "--help":
                    options.ShowHelp = true;
                    return options;

                case "--json":
                    options.Json = true;
                    break;

                case "--yes" or "-y":
                    options.Yes = true;
                    break;

                case "--install":
                    options.ScheduleInstall = true;
                    break;

                case "--remove":
                    options.ScheduleRemove = true;
                    break;

                case "--daily":
                    options.ScheduleFrequency = ScheduleFrequency.Daily;
                    break;

                case "--weekly":
                    options.ScheduleFrequency = ScheduleFrequency.Weekly;
                    break;

                case "--status":
                    break;

                case "--origin":
                    if (!TryTakeValue(args, ref index, argument, separator, name, out var origin, out error))
                    {
                        return null;
                    }

                    if (!IsValidOrigin(origin))
                    {
                        error = $"--origin expects a short label with no spaces (got '{origin}').";
                        return null;
                    }

                    options.Origin = origin;
                    break;

                case "--target":
                    if (!TryTakeValue(args, ref index, argument, separator, name, out var target, out error))
                    {
                        return null;
                    }

                    options.Targets.Add(target);
                    break;

                case "--min-age":
                    if (!TryTakeValue(args, ref index, argument, separator, name, out var ageText, out error))
                    {
                        return null;
                    }

                    if (!double.TryParse(ageText, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
                        || minutes < 0)
                    {
                        error = $"--min-age expects a non-negative number of minutes (got '{ageText}').";
                        return null;
                    }

                    options.MinimumAge = TimeSpan.FromMinutes(minutes);
                    break;

                case "--mode":
                    if (!TryTakeValue(args, ref index, argument, separator, name, out var modeText, out error))
                    {
                        return null;
                    }

                    if (!TryParseMode(modeText, out var mode))
                    {
                        error = $"--mode expects 'recycle' or 'permanent' (got '{modeText}').";
                        return null;
                    }

                    options.Mode = mode;
                    break;

                case "--top":
                    if (!TryTakeValue(args, ref index, argument, separator, name, out var topText, out error))
                    {
                        return null;
                    }

                    if (!int.TryParse(topText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var top) || top < 0)
                    {
                        error = $"--top expects a non-negative integer (got '{topText}').";
                        return null;
                    }

                    options.Top = top;
                    break;

                case "--at":
                    if (!TryTakeValue(args, ref index, argument, separator, name, out var timeText, out error))
                    {
                        return null;
                    }

                    if (!TimeOnly.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                    {
                        error = $"--at expects a time of day such as 03:00 (got '{timeText}').";
                        return null;
                    }

                    options.ScheduleTime = time.ToTimeSpan();
                    break;

                case "--day":
                    if (!TryTakeValue(args, ref index, argument, separator, name, out var dayText, out error))
                    {
                        return null;
                    }

                    if (!TryParseDay(dayText, out var day))
                    {
                        error = $"--day expects a weekday such as SUN or Sunday (got '{dayText}').";
                        return null;
                    }

                    options.ScheduleDay = day;
                    break;

                default:
                    if (name.StartsWith('-'))
                    {
                        error = $"Unknown option '{name}'.";
                        return null;
                    }

                    positional.Add(argument);
                    break;
            }
        }

        if (positional.Count == 0)
        {
            error = "No command given.";
            return null;
        }

        options.Command = positional[0].ToLowerInvariant() switch
        {
            "scan" => CliCommand.Scan,
            "clean" => CliCommand.Clean,
            "schedule" => CliCommand.Schedule,
            _ => CliCommand.None,
        };

        if (options.Command == CliCommand.None)
        {
            error = $"Unknown command '{positional[0]}'. Expected 'scan', 'clean' or 'schedule'.";
            return null;
        }

        if (positional.Count > 1)
        {
            error = $"Unexpected argument '{positional[1]}'.";
            return null;
        }

        if (options.ScheduleInstall && options.ScheduleRemove)
        {
            error = "Use either --install or --remove, not both.";
            return null;
        }

        if (options.Command != CliCommand.Schedule
            && (options.ScheduleInstall || options.ScheduleRemove || options.ScheduleFrequency == ScheduleFrequency.Weekly))
        {
            error = "--install, --remove and --weekly only apply to the 'schedule' command.";
            return null;
        }

        if (options.Origin is not null && options.Command != CliCommand.Clean)
        {
            error = "--origin only applies to the 'clean' command, which is what records a history entry.";
            return null;
        }

        return options;
    }

    private static bool TryTakeValue(
        string[] args,
        ref int index,
        string argument,
        int separator,
        string name,
        out string value,
        out string? error)
    {
        error = null;

        if (separator >= 0)
        {
            value = argument[(separator + 1)..];
            if (value.Length == 0)
            {
                error = $"Option '{name}' requires a value.";
                return false;
            }

            return true;
        }

        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            error = $"Option '{name}' requires a value.";
            return false;
        }

        value = args[++index];
        return true;
    }

    /// <summary>
    /// A label is written into a JSON history file and shown in the app, so it is kept short and
    /// free of spaces rather than accepting anything the shell happened to hand over.
    /// </summary>
    private static bool IsValidOrigin(string text) =>
        text.Length is > 0 and <= 40
        && !text.Any(char.IsWhiteSpace)
        && !text.Contains('"', StringComparison.Ordinal);

    private static bool TryParseMode(string text, out DeletionMode mode)
    {
        switch (text.ToLowerInvariant())
        {
            case "recycle" or "recyclebin" or "recycle-bin":
                mode = DeletionMode.RecycleBin;
                return true;
            case "permanent" or "delete":
                mode = DeletionMode.Permanent;
                return true;
            default:
                mode = DeletionMode.RecycleBin;
                return false;
        }
    }

    /// <summary>Accepts a full weekday name or the three-letter form the scheduler itself uses.</summary>
    private static bool TryParseDay(string text, out DayOfWeek day)
    {
        if (Enum.TryParse(text, ignoreCase: true, out day) && Enum.IsDefined(day))
        {
            return true;
        }

        var prefix = text.Trim().ToUpperInvariant();
        day = prefix switch
        {
            "MON" => DayOfWeek.Monday,
            "TUE" => DayOfWeek.Tuesday,
            "WED" => DayOfWeek.Wednesday,
            "THU" => DayOfWeek.Thursday,
            "FRI" => DayOfWeek.Friday,
            "SAT" => DayOfWeek.Saturday,
            "SUN" => DayOfWeek.Sunday,
            _ => (DayOfWeek)(-1),
        };

        return (int)day >= 0;
    }
}
