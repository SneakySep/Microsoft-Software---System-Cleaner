using WindowsCleaner.Core.Models;

namespace WindowsCleaner.Core.Utilities;

/// <summary>Turns a <see cref="SkipReason"/> into the short phrase shown to a user.</summary>
/// <remarks>
/// Shared by the desktop app and the CLI so the two can never drift into describing the same
/// condition differently.
/// </remarks>
public static class SkipReasonFormatter
{
    public static string Describe(SkipReason reason) => reason switch
    {
        SkipReason.Locked => "in use",
        SkipReason.AccessDenied => "access denied",
        SkipReason.TooRecent => "modified too recently",
        SkipReason.ReparsePoint => "links/junctions",
        SkipReason.NotFound => "vanished during scan",
        SkipReason.UnsafePath => "outside the allowlist",
        _ => "unreadable",
    };
}
