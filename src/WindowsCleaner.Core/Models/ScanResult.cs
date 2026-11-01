namespace WindowsCleaner.Core.Models;

/// <summary>Per-target outcome, so the UI can show which locations were reachable.</summary>
public sealed class TargetSummary
{
    public required string DisplayName { get; init; }
    public required string Path { get; init; }

    /// <summary>True when the directory existed and could be enumerated.</summary>
    public required bool Accessible { get; init; }

    /// <summary>Populated when <see cref="Accessible"/> is false (for example "Access denied").</summary>
    public string? Error { get; init; }

    public required int FileCount { get; init; }
    public required long TotalBytes { get; init; }

    /// <summary>True when the user added this folder themselves rather than it being standard.</summary>
    public bool IsUserAdded { get; init; }
}

/// <summary>The complete result of a scan.</summary>
public sealed class ScanResult
{
    public IReadOnlyList<JunkFile> Files { get; init; } = [];

    public long TotalSizeBytes { get; init; }

    public TimeSpan Duration { get; init; }

    public IReadOnlyList<TargetSummary> Targets { get; init; } = [];

    /// <summary>
    /// Count of entries that were skipped, grouped by reason. Surfaced in the UI so that
    /// "nothing found" is never ambiguous: the user can see that 400 files were skipped
    /// because they were locked, for example.
    /// </summary>
    public IReadOnlyDictionary<SkipReason, int> Skipped { get; init; } =
        new Dictionary<SkipReason, int>();

    public int SkippedCount => Skipped.Values.Sum();
}
