namespace WindowsCleaner.Core.Models;

/// <summary>
/// A progress snapshot pushed to the UI during a scan.
/// </summary>
/// <remarks>
/// This is a small immutable record because it is marshalled across threads via
/// <see cref="IProgress{T}"/>. Reporting on every single file would flood the dispatcher,
/// so the scanner throttles by count and by elapsed time.
/// </remarks>
public sealed record ScanProgress(
    string TargetName,
    int TargetIndex,
    int TargetCount,
    int FilesFound,
    long BytesFound,
    string? CurrentPath = null);
