namespace WindowsCleaner.Core.Models;

/// <summary>A progress snapshot pushed to the UI while files are being disposed of.</summary>
public sealed record CleanProgress(
    int Processed,
    int Total,
    int Deleted,
    int Recycled,
    int Failed,
    long FreedBytes,
    long RecycledBytes,
    string? CurrentPath = null);
