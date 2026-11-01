namespace WindowsCleaner.Core.Models;

/// <summary>
/// Which front-end ran a clean, and whether it was unattended.
/// </summary>
/// <remarks>
/// Recorded on every report and shown in the history. The distinction that earns its keep is
/// <see cref="Scheduled"/>: a run nobody watched is the one worth being able to find afterwards,
/// and telling it apart from a command somebody typed needs the origin to be stated at the time
/// rather than guessed later from the command line.
/// </remarks>
public static class CleanReportSources
{
    /// <summary>The desktop app's own Clean Now.</summary>
    public const string App = "app";

    /// <summary>A clean somebody ran at the command line.</summary>
    public const string Cli = "cli";

    /// <summary>A clean run by the scheduled job.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>Displayable label for an origin, tolerating one this build does not know.</summary>
    public static string Describe(string source) => source switch
    {
        App => "app",
        Cli => "command line",
        Scheduled => "scheduled",
        _ => source,
    };
}
