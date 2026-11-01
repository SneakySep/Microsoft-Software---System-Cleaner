namespace WindowsCleaner.Core.Models;

/// <summary>
/// A single allowlisted location that the cleaner is permitted to inspect and empty.
/// <para>
/// The cleaner only ever touches the <em>contents</em> of <see cref="Path"/>. The directory
/// itself is never deleted, because several of these directories (notably <c>%TEMP%</c>)
/// are required by Windows and by running applications.
/// </para>
/// </summary>
public sealed class CleanTarget
{
    /// <summary>Friendly name shown in the UI, e.g. "User temporary files".</summary>
    public required string DisplayName { get; init; }

    /// <summary>Absolute path to the directory. Expansion of environment variables happens in the provider.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// True when emptying this location normally requires an elevated token
    /// (for example <c>C:\Windows\Temp</c>). Purely informational: the scanner
    /// reports the location as inaccessible rather than crashing when not elevated.
    /// </summary>
    public bool RequiresElevation { get; init; }

    /// <summary>
    /// True when the user added this folder themselves rather than it being one of the
    /// standard Windows locations. Surfaced so a report can distinguish the two.
    /// </summary>
    public bool IsUserAdded { get; init; }
}
