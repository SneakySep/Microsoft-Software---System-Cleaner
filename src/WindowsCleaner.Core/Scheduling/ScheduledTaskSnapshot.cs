using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using WindowsCleaner.Core.Models;

namespace WindowsCleaner.Core.Scheduling;

/// <summary>
/// What Task Scheduler actually has registered for our job.
/// </summary>
/// <remarks>
/// <para>
/// Read back from the scheduler rather than remembered from the last time the app wrote it. The
/// job can be changed by hand (<c>schtasks</c>, Task Scheduler's own UI, a group policy), and a
/// panel that reported its own last request would then be quietly wrong about a job that runs
/// unattended and deletes files.
/// </para>
/// <para><b>Why the XML is parsed and not the text listing.</b> <c>schtasks /Query /FO LIST</c>
/// prints localised labels ("Task To Run" and so on), so anything parsing it breaks on a
/// non-English Windows. The task definition's element names are fixed identifiers in the
/// scheduler's schema regardless of the display language, so they can be matched literally.</para>
/// </remarks>
public sealed record ScheduledTaskSnapshot
{
    public required ScheduleFrequency Frequency { get; init; }

    public required TimeSpan TimeOfDay { get; init; }

    /// <summary>The day for a weekly schedule; null when the definition does not name one.</summary>
    public DayOfWeek? Day { get; init; }

    /// <summary>
    /// How often the schedule repeats: days for a daily trigger, weeks for a weekly one.
    /// </summary>
    /// <remarks>
    /// This app always registers an interval of 1. Anything else means the job was edited
    /// elsewhere, which is worth knowing because it changes when the job runs.
    /// </remarks>
    public int Interval { get; init; } = 1;

    /// <summary>
    /// When the schedule started, as stored by the scheduler.
    /// </summary>
    /// <remarks>
    /// Kept because an interval greater than 1 counts from this date, not from today.
    /// </remarks>
    public DateTimeOffset StartBoundary { get; init; }

    /// <summary>The executable the task runs, with the surrounding quotes stripped.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>The arguments as stored in the task definition.</summary>
    public required string Arguments { get; init; }

    /// <summary>
    /// Disposal mode recovered from the stored command line.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="DeletionMode.RecycleBin"/> when the arguments do not say, because
    /// that is what the command actually does: it is the CLI's own default for <c>clean</c>. This
    /// has to describe the command rather than guessing at something safer, since the app repeats
    /// it in a confirmation before running the job - and a prompt claiming permanent deletion
    /// while the job quietly recycles would be wrong in the one direction that matters, telling
    /// somebody files are gone when they are still recoverable.
    /// </remarks>
    public DeletionMode Mode { get; init; } = DeletionMode.RecycleBin;

    /// <summary>Freshness guard recovered from the stored command line; null when absent.</summary>
    public TimeSpan? MinimumAge { get; init; }

    /// <summary>Human-readable schedule, for example "every Thursday at 21:30".</summary>
    public string DescribeSchedule()
    {
        var time = ScheduleText.FormatTimeOfDay(TimeOfDay);
        var repetition = Interval > 1
            ? Frequency == ScheduleFrequency.Weekly ? $"every {Interval} weeks" : $"every {Interval} days"
            : Frequency == ScheduleFrequency.Weekly ? "every week" : "every day";

        if (Frequency == ScheduleFrequency.Weekly && Interval == 1 && Day is { } day)
        {
            return $"every {day} at {time}";
        }

        return $"{repetition} at {time}";
    }

    /// <summary>The full command the scheduler will run, for display.</summary>
    public string DescribeCommand() => $"{ExecutablePath} {Arguments}".Trim();

    /// <summary>
    /// When the job is next due, or null when that cannot be worked out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Computed rather than read from the scheduler, whose summary of the next run time is
    /// localised text. Computing it also keeps the panel honest about a job that has been edited:
    /// it answers from the same definition it displays.
    /// </para>
    /// <para>
    /// A repeat interval greater than one day returns null. Those runs are counted from the
    /// original start date, and working that out from a boundary that may be months old invites a
    /// confidently wrong answer; the schedule itself is still described in words.
    /// </para>
    /// <para>
    /// The offset used is the one in force at <paramref name="now"/>, so a run that falls on the
    /// far side of a daylight-saving change can be an hour out. This is a label on a dialog, not
    /// an agreement about when to delete files, so that is an acceptable trade for not modelling
    /// time zones here.
    /// </para>
    /// </remarks>
    public DateTimeOffset? GetNextRun(DateTimeOffset now)
    {
        if (Interval != 1)
        {
            return null;
        }

        var date = DateOnly.FromDateTime(now.LocalDateTime);
        var time = TimeOnly.FromTimeSpan(TimeOfDay);

        if (Frequency == ScheduleFrequency.Daily)
        {
            var today = new DateTimeOffset(date.ToDateTime(time), now.Offset);
            return today > now ? today : today.AddDays(1);
        }

        if (Day is not { } day)
        {
            return null;
        }

        var daysAhead = ((int)day - (int)date.DayOfWeek + 7) % 7;
        var candidate = new DateTimeOffset(date.AddDays(daysAhead).ToDateTime(time), now.Offset);

        return candidate > now ? candidate : candidate.AddDays(7);
    }

    /// <summary>
    /// Splits the stored argument string into separate arguments, honouring double quotes.
    /// </summary>
    /// <remarks>
    /// Needed to run the job's own command, because the definition stores its arguments as one
    /// string: handing that string over whole would turn a quoted path containing a space into
    /// several arguments. An empty quoted argument is kept, since dropping it would shift every
    /// argument after it.
    /// </remarks>
    public static IReadOnlyList<string> SplitArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return [];
        }

        var parsed = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var started = false;

        foreach (var character in arguments)
        {
            if (character == '"')
            {
                // The quote itself is not part of the value.
                inQuotes = !inQuotes;
                started = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(character))
            {
                if (started)
                {
                    parsed.Add(current.ToString());
                    current.Clear();
                    started = false;
                }

                continue;
            }

            current.Append(character);
            started = true;
        }

        if (started)
        {
            parsed.Add(current.ToString());
        }

        return parsed;
    }

    /// <summary>
    /// Parses a task definition produced by <c>schtasks /Query /XML</c>.
    /// </summary>
    /// <returns>False with <paramref name="error"/> set when the definition cannot be understood.</returns>
    public static bool TryParse(string? xml, out ScheduledTaskSnapshot? snapshot, out string? error)
    {
        snapshot = null;
        error = null;

        if (string.IsNullOrWhiteSpace(xml))
        {
            error = "The scheduler did not return a task definition.";
            return false;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException e)
        {
            error = $"Could not read the task definition: {e.Message}";
            return false;
        }

        var root = document.Root;
        if (root is null)
        {
            error = "The task definition is empty.";
            return false;
        }

        var ns = root.Name.Namespace;

        var exec = root.Element(ns + "Actions")?.Element(ns + "Exec");
        if (exec is null)
        {
            error = "The scheduled task has no executable action to run.";
            return false;
        }

        var command = Unquote(exec.Element(ns + "Command")?.Value?.Trim() ?? string.Empty);
        if (command.Length == 0)
        {
            error = "The scheduled task does not say what to run.";
            return false;
        }

        var arguments = exec.Element(ns + "Arguments")?.Value?.Trim() ?? string.Empty;

        var trigger = root.Element(ns + "Triggers")?.Element(ns + "CalendarTrigger");
        if (trigger is null)
        {
            error = "The scheduled task has no calendar trigger.";
            return false;
        }

        if (!TryParseStartBoundary(trigger.Element(ns + "StartBoundary")?.Value?.Trim(), out var startBoundary))
        {
            error = "The scheduled task does not have a readable start time.";
            return false;
        }

        ScheduleFrequency frequency;
        DayOfWeek? day = null;
        var interval = 1;

        if (trigger.Element(ns + "ScheduleByWeek") is { } weekly)
        {
            frequency = ScheduleFrequency.Weekly;
            interval = ReadInterval(weekly.Element(ns + "WeeksInterval"));

            // The scheduler writes the day as an empty element named after it, e.g. <Thursday />.
            var dayName = weekly.Element(ns + "DaysOfWeek")?.Elements().FirstOrDefault()?.Name.LocalName;
            if (dayName is not null && Enum.TryParse<DayOfWeek>(dayName, ignoreCase: true, out var parsed))
            {
                day = parsed;
            }
        }
        else if (trigger.Element(ns + "ScheduleByDay") is { } daily)
        {
            frequency = ScheduleFrequency.Daily;
            interval = ReadInterval(daily.Element(ns + "DaysInterval"));
        }
        else
        {
            error = "The scheduled task uses a trigger type this app does not create (only daily "
                + "and weekly schedules are understood).";
            return false;
        }

        ParseArguments(arguments, out var specifiedMode, out var minimumAge);

        snapshot = new ScheduledTaskSnapshot
        {
            Frequency = frequency,
            TimeOfDay = startBoundary.TimeOfDay,
            Day = day,
            Interval = interval,
            StartBoundary = startBoundary,
            ExecutablePath = command,
            Arguments = arguments,
            MinimumAge = minimumAge,
        };

        // Overridden only when the command spells it out, so the default for "the command does not
        // say" lives in exactly one place: the property itself.
        if (specifiedMode is { } mode)
        {
            snapshot = snapshot with { Mode = mode };
        }

        return true;
    }

    /// <summary>Reads an interval, falling back to 1 for anything missing or nonsensical.</summary>
    private static int ReadInterval(XElement? element) =>
        int.TryParse(element?.Value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value > 0
            ? value
            : 1;

    private static bool TryParseStartBoundary(string? value, out DateTimeOffset startBoundary)
    {
        startBoundary = default;

        // The stored boundary carries no offset; it is local wall-clock time, which is exactly
        // what the scheduler runs on.
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out startBoundary);
    }

    /// <summary>
    /// Recovers the disposal mode and freshness guard from the stored command line, which is
    /// the only place the task definition records them.
    /// </summary>
    private static void ParseArguments(string arguments, out DeletionMode? mode, out TimeSpan? minimumAge)
    {
        mode = null;
        minimumAge = null;

        var tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var index = 0; index < tokens.Length; index++)
        {
            // Accepts both "--mode permanent" and "--mode=permanent", the same as the CLI does.
            // Anything not recognised here is left alone, so a job edited to add its own options
            // still reads back correctly.
            var separator = tokens[index].IndexOf('=');
            var name = separator >= 0 ? tokens[index][..separator] : tokens[index];

            switch (name)
            {
                case "--mode":
                    if (TryReadValue(tokens, ref index, separator) is { } modeText)
                    {
                        mode = modeText.Equals("recycle", StringComparison.OrdinalIgnoreCase)
                            ? DeletionMode.RecycleBin
                            : DeletionMode.Permanent;
                    }

                    break;

                case "--min-age":
                    if (TryReadValue(tokens, ref index, separator) is { } ageText
                        && double.TryParse(ageText, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
                        && minutes >= 0)
                    {
                        minimumAge = TimeSpan.FromMinutes(minutes);
                    }

                    break;
            }
        }
    }

    private static string? TryReadValue(string[] tokens, ref int index, int separator)
    {
        if (separator >= 0)
        {
            return tokens[index][(separator + 1)..];
        }

        if (index + 1 >= tokens.Length)
        {
            return null;
        }

        return tokens[++index];
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;
}
