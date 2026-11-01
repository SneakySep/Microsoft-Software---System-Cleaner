namespace WindowsCleaner.Core.Scheduling;

/// <summary>
/// Finds the command-line front-end, <c>wclean.exe</c>.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> The scheduled job runs <c>wclean clean</c>, so the desktop app
/// has to register *that* executable. Scheduling the desktop app instead would produce a job
/// that silently does nothing useful: it would launch the GUI at 3am, the GUI would ignore the
/// arguments, and nothing would ever be cleaned — a failure that is invisible precisely because
/// it happens unattended.</para>
/// <para>Checked in order: an explicit override, then the app's own folder (a published layout
/// puts both executables side by side), then build outputs found by walking up from the app's
/// folder (the layout of a source checkout, where this app is run straight out of
/// <c>bin</c>).</para>
/// </remarks>
public static class WcleanLocator
{
    public const string ExecutableName = "wclean.exe";

    /// <summary>Environment variable that overrides the search entirely.</summary>
    public const string PathVariable = "WINDOWSCLEANER_WCLEAN";

    /// <summary>How far up from the app's folder to look for a source checkout.</summary>
    private const int DefaultAncestorLevels = 6;

    /// <summary>
    /// The build-output folders of a source checkout, relative to a candidate ancestor.
    /// </summary>
    private static readonly string[] BuildOutputRoots =
    [
        Path.Combine("src", "WindowsCleaner.Cli", "bin"),
        Path.Combine("WindowsCleaner.Cli", "bin"),
    ];

    /// <summary>Locates the CLI relative to the running process, honouring the override variable.</summary>
    public static bool TryLocate(out string? path, out IReadOnlyList<string> searched) =>
        TryLocateFrom(
            StartDirectory(),
            DefaultAncestorLevels,
            Environment.GetEnvironmentVariable(PathVariable),
            out path,
            out searched);

    /// <summary>
    /// The search itself, with every input supplied so it can be exercised against a sandbox
    /// directory in tests rather than whatever happens to sit above the test runner.
    /// </summary>
    /// <param name="startDirectory">Folder to start from; walked upwards.</param>
    /// <param name="ancestorLevels">How many parent folders to inspect as well.</param>
    /// <param name="overridePath">Explicit path that wins if it exists; usually from the environment.</param>
    /// <param name="searched">Where the search looked, for when it comes up empty.</param>
    public static bool TryLocateFrom(
        string startDirectory,
        int ancestorLevels,
        string? overridePath,
        out string? path,
        out IReadOnlyList<string> searched)
    {
        var looked = new List<string>();

        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            looked.Add(overridePath);

            if (File.Exists(overridePath))
            {
                path = Path.GetFullPath(overridePath);
                searched = looked;
                return true;
            }
        }

        var directory = TryDescribe(startDirectory);

        for (var level = 0; directory is not null && level <= Math.Max(0, ancestorLevels); level++)
        {
            var basePath = directory.FullName;

            var beside = Path.Combine(basePath, ExecutableName);
            if (Record(looked, beside, out var found))
            {
                path = found;
                searched = looked;
                return true;
            }

            // A published layout may put the CLI in its own subfolder.
            var inCliFolder = Path.Combine(basePath, "cli", ExecutableName);
            if (Record(looked, inCliFolder, out found))
            {
                path = found;
                searched = looked;
                return true;
            }

            foreach (var candidate in SearchBuildOutputs(basePath, looked))
            {
                path = candidate;
                searched = looked;
                return true;
            }

            directory = directory.Parent;
        }

        path = null;
        searched = looked;
        return false;
    }

    private static bool Record(List<string> looked, string candidate, out string? found)
    {
        looked.Add(candidate);
        found = null;

        if (!File.Exists(candidate))
        {
            return false;
        }

        found = Path.GetFullPath(candidate);
        return true;
    }

    /// <summary>
    /// Looks inside a source checkout's CLI build folders, newest build first so a stale Release
    /// output does not beat the Debug build that was just produced.
    /// </summary>
    private static IEnumerable<string> SearchBuildOutputs(string basePath, List<string> looked)
    {
        foreach (var relative in BuildOutputRoots)
        {
            var bin = Path.Combine(basePath, relative);
            if (!Directory.Exists(bin))
            {
                continue;
            }

            looked.Add(Path.Combine(bin, "**", ExecutableName));

            FileInfo[] files;
            try
            {
                files = new DirectoryInfo(bin).GetFiles(ExecutableName, SearchOption.AllDirectories);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc))
            {
                yield return file.FullName;
            }
        }
    }

    private static DirectoryInfo? TryDescribe(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            return new DirectoryInfo(Path.GetFullPath(directory));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string StartDirectory()
    {
        var processPath = Environment.ProcessPath;
        var directory = string.IsNullOrWhiteSpace(processPath) ? null : Path.GetDirectoryName(processPath);

        return string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory;
    }
}
