using WindowsCleaner.Core.Safety;

namespace WindowsCleaner.Core.Targets;

/// <summary>
/// Screens user-supplied folders before they are accepted as cleaning roots.
/// </summary>
/// <remarks>
/// <para>
/// Letting a user add arbitrary folders makes the tool strictly more useful, and strictly more
/// capable of destroying something important. This validator is the guard rail: it refuses
/// roots that are system or personal locations, or that <em>contain</em> one.
/// </para>
/// <para>
/// The rule is deliberately about whole folders rather than a blanket ban on a directory tree.
/// Adding <c>C:\Windows\Temp</c> is fine even though it lives under <c>C:\Windows</c>, because
/// it is neither equal to a protected root nor an ancestor of one. Adding <c>C:\Windows</c> or
/// <c>C:\Users</c> is refused, because everything inside them would become deletable.
/// </para>
/// </remarks>
public static class SafeRootValidator
{
    private static readonly Lazy<IReadOnlyList<string>> ProtectedRoots = new(BuildProtectedRoots);

    /// <summary>The folders that may never be used as a cleaning root.</summary>
    public static IReadOnlyList<string> Protected => ProtectedRoots.Value;

    /// <summary>
    /// Decides whether <paramref name="path"/> is acceptable as a user-added cleaning root.
    /// </summary>
    /// <returns>True when the folder may be added; otherwise false with a reason for the user.</returns>
    public static bool IsAllowed(string? path, out string? reason)
    {
        reason = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "No folder was given.";
            return false;
        }

        // Checked before normalizing, deliberately. GetFullPath would happily resolve a
        // relative path against whatever the current directory happens to be, which would turn
        // an ambiguous input into a silently-wrong cleaning root.
        bool isAbsolute;
        try
        {
            isAbsolute = Path.IsPathFullyQualified(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            reason = "That is not a valid folder path.";
            return false;
        }

        if (!isAbsolute)
        {
            reason = "The path must be absolute.";
            return false;
        }

        string full;
        try
        {
            full = PathGuard.NormalizeRoot(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "That is not a valid folder path.";
            return false;
        }

        foreach (var protectedRoot in Protected)
        {
            if (full.Equals(protectedRoot, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"'{full}' is a system or personal folder. Cleaning it wholesale is not allowed.";
                return false;
            }

            // Refuse a folder that contains a protected one, so you cannot reach Documents by
            // adding the profile directory above it.
            if (PathGuard.IsStrictlyWithin(protectedRoot, full))
            {
                reason = $"'{full}' contains '{protectedRoot}', which must not be cleaned.";
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<string> BuildProtectedRoots()
    {
        var roots = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var normalized = PathGuard.NormalizeRoot(path);
                if (Path.IsPathFullyQualified(normalized) && !roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(normalized);
                }
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A folder this environment cannot resolve simply is not protected by name.
            }
        }

        // Whole volumes.
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady)
                {
                    Add(drive.RootDirectory.FullName);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Add(windows);
        Add(Path.Combine(windows, "System32"));
        Add(Path.Combine(windows, "SysWOW64"));
        Add(Path.Combine(windows, "WinSxS"));
        Add(Path.Combine(windows, "assembly"));
        Add(Path.Combine(windows, "Fonts"));
        Add(Path.Combine(windows, "drivers"));

        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(profile);

        // Personal folders get named individually so that the profile root is refused while
        // AppData\Local\Temp deeper down remains usable.
        Add(Path.Combine(profile, "Desktop"));
        Add(Path.Combine(profile, "Documents"));
        Add(Path.Combine(profile, "Downloads"));
        Add(Path.Combine(profile, "Pictures"));
        Add(Path.Combine(profile, "Videos"));
        Add(Path.Combine(profile, "Music"));
        Add(Path.Combine(profile, "OneDrive"));
        Add(Path.Combine(profile, "Favorites"));

        Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        return roots;
    }
}
