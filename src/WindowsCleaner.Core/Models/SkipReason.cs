namespace WindowsCleaner.Core.Models;

/// <summary>
/// Why a file system entry was deliberately left alone during a scan.
/// </summary>
/// <remarks>
/// These are expected, per-file conditions rather than errors. A cleaner that walks
/// <c>%TEMP%</c> will always meet locked and permission-denied entries; the point is to
/// count and continue rather than abort the whole scan.
/// </remarks>
public enum SkipReason
{
    /// <summary>The file is open by another process with no sharing (delete would fail).</summary>
    Locked,

    /// <summary>Access was denied by the file system ACLs.</summary>
    AccessDenied,

    /// <summary>The file was modified too recently to be considered stale (see <c>ScannerOptions.MinimumAge</c>).</summary>
    TooRecent,

    /// <summary>A symlink, junction or other reparse point. Never followed, never deleted through.</summary>
    ReparsePoint,

    /// <summary>The path disappeared between enumeration and inspection.</summary>
    NotFound,

    /// <summary>A path that failed the allowlist check in <c>PathGuard</c>.</summary>
    UnsafePath,

    /// <summary>An unexpected I/O error. Recorded so it is visible rather than silently swallowed.</summary>
    Error,
}
