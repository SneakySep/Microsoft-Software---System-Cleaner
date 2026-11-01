using System.Runtime.CompilerServices;

namespace WindowsCleaner.Core.Safety;

/// <summary>Raised when a path fails the allowlist check.</summary>
public sealed class UnsafePathException(string path, string reason)
    : Exception($"Refusing to operate on '{path}': {reason}")
{
    public string AttemptedPath { get; } = path;
    public string Reason { get; } = reason;
}

/// <summary>
/// The single choke point that decides whether a path may be inspected or deleted.
/// </summary>
/// <remarks>
/// <para>
/// A deletion tool is only as safe as its path validation, so every guard lives here and
/// every destructive call site must go through it. The checks are:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Allowlist.</b> The path must sit strictly *inside* one of the configured roots.
///     Being equal to a root is rejected, which is what stops the cleaner from deleting
///     <c>%TEMP%</c> itself.
///   </description></item>
///   <item><description>
///     <b>Canonicalization.</b> The path is fully resolved first, so <c>..</c> segments and
///     relative paths cannot escape the root after the fact.
///   </description></item>
///   <item><description>
///     <b>Reparse points.</b> Symlinks and junctions are never followed or deleted through.
///     A junction planted in <c>%TEMP%</c> pointing at <c>C:\Users</c> would otherwise turn
///     a temp clean into data loss, and the lexical checks alone would not catch it.
///   </description></item>
/// </list>
/// <para>
/// Validation runs both when a file is discovered and again immediately before deletion.
/// The second check is what makes the cleaner safe against a path being swapped between
/// scan and clean (a time-of-check/time-of-use race).
/// </para>
/// </remarks>
public static class PathGuard
{
    /// <summary>
    /// Normalizes a directory path to a canonical, separator-trimmed absolute form
    /// suitable for prefix comparison.
    /// </summary>
    public static string NormalizeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);

        // GetFullPath on Windows already collapses ".." and normalizes separators, but it
        // preserves a trailing separator. Trim it so prefix math is predictable.
        return Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>
    /// True when <paramref name="candidatePath"/> is strictly inside <paramref name="rootPath"/>.
    /// Equal paths return false: the root directory itself is never a valid target.
    /// </summary>
    public static bool IsStrictlyWithin(string candidatePath, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(candidatePath);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Malformed or over-long path: treat as unsafe rather than throwing to the caller.
            return false;
        }

        var root = NormalizeRoot(rootPath);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (candidate.Equals(root, comparison))
        {
            return false; // The root itself is off limits.
        }

        // A drive root such as "C:\" already ends with a separator, so appending another
        // would build "C:\\" and fail to match every path beneath it.
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(prefix, comparison))
        {
            return false;
        }

        // Defence in depth: reject any surviving parent-directory segment. GetFullPath should
        // have removed these, so reaching this branch means something is malformed.
        var relative = candidate[prefix.Length..];
        return !relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is ".." or ".");
    }

    /// <summary>
    /// Throws <see cref="UnsafePathException"/> unless the path is strictly within one of
    /// <paramref name="allowedRoots"/>.
    /// </summary>
    public static void EnsureWithin(string candidatePath, IReadOnlyList<string> allowedRoots)
    {
        foreach (var root in allowedRoots)
        {
            if (IsStrictlyWithin(candidatePath, root))
            {
                return;
            }
        }

        throw new UnsafePathException(candidatePath, "path is not inside any allowlisted cleaning root");
    }

    /// <inheritdoc cref="EnsureWithin(string, IReadOnlyList{string})"/>
    public static void EnsureWithin(string candidatePath, params string[] allowedRoots) =>
        EnsureWithin(candidatePath, (IReadOnlyList<string>)allowedRoots);

    /// <summary>
    /// Returns true when the entry is a reparse point (symlink, junction, mount point,
    /// OneDrive placeholder, and so on) and therefore must not be traversed or deleted.
    /// </summary>
    public static bool IsReparsePoint(FileAttributes attributes) =>
        attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>Outcome of walking a path's ancestors looking for a reparse point.</summary>
    public enum ReparsePointScan
    {
        /// <summary>The path resolves through ordinary directories only.</summary>
        Clean,

        /// <summary>A symlink or junction was found in the chain; the path must not be used.</summary>
        Found,

        /// <summary>The chain could not be inspected at all (missing file, denied ancestor).</summary>
        Indeterminate,
    }

    /// <summary>
    /// Walks every ancestor from <paramref name="candidatePath"/> up to (but excluding)
    /// <paramref name="rootPath"/> and reports the first reparse point found.
    /// </summary>
    /// <remarks>
    /// This closes the junction-escape hole left by purely lexical validation: a path can be
    /// textually inside <c>%TEMP%</c> while physically resolving to somewhere else entirely.
    /// <para>
    /// <see cref="ReparsePointScan.Indeterminate"/> is deliberately distinct from
    /// <see cref="ReparsePointScan.Found"/>. A path that cannot be stat'ed at all (the file was
    /// deleted a moment ago, or an ancestor is unreadable) is not a junction escape: an actual
    /// delete against it fails on its own. Collapsing the two would reject harmless paths and
    /// report the wrong reason for them.
    /// </para>
    /// </remarks>
    public static ReparsePointScan ScanReparseChain(string candidatePath, string rootPath, out string? reparsePoint)
    {
        reparsePoint = null;
        var root = NormalizeRoot(rootPath);

        var current = candidatePath;
        while (!string.IsNullOrEmpty(current))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // FileNotFoundException and DirectoryNotFoundException derive from IOException,
                // so this covers a vanished file as well as a denied one.
                return ReparsePointScan.Indeterminate;
            }

            if (IsReparsePoint(attributes))
            {
                reparsePoint = current;
                return ReparsePointScan.Found;
            }

            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            if (parent is null || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            // Reached the root: the root itself is trusted, so stop climbing.
            if (parent.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return ReparsePointScan.Clean;
    }

    /// <summary>
    /// The full gate used before any destructive operation: canonical form, allowlist
    /// membership, and reparse-point ancestry.
    /// </summary>
    /// <returns>A non-null reason string when the path must be rejected, otherwise null.</returns>
    public static string? GetRejectionReason(string candidatePath, IReadOnlyList<string> allowedRoots)
    {
        var matchedRoot = allowedRoots.FirstOrDefault(root => IsStrictlyWithin(candidatePath, root));
        if (matchedRoot is null)
        {
            return "outside the allowlisted cleaning roots";
        }

        return ScanReparseChain(candidatePath, matchedRoot, out var reparsePoint) switch
        {
            ReparsePointScan.Found => $"path passes through reparse point '{reparsePoint}'",

            // Indeterminate is not a rejection: the subsequent delete fails safely on its own,
            // and reporting it as an escape attempt would be a lie about what happened.
            _ => null,
        };
    }

    /// <summary>True when the path is safe to delete under the supplied roots.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSafeToDelete(string candidatePath, IReadOnlyList<string> allowedRoots) =>
        GetRejectionReason(candidatePath, allowedRoots) is null;
}
