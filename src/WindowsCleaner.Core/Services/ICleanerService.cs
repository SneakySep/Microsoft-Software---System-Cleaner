using WindowsCleaner.Core.Models;

namespace WindowsCleaner.Core.Services;

/// <summary>Disposes of previously scanned files and reports what happened to them.</summary>
public interface ICleanerService
{
    /// <summary>
    /// Removes each supplied file, deleting it or sending it to the Recycle Bin according to
    /// <see cref="CleanOptions.Mode"/>. Files that fail are skipped and reported.
    /// </summary>
    /// <remarks>
    /// Every path is revalidated against the allowlist and re-statted immediately before it is
    /// touched, so a file list that is stale or was tampered with cannot direct the cleaner
    /// outside its roots. Individual failures are reported in
    /// <see cref="CleanResult.Failures"/> rather than thrown.
    /// </remarks>
    Task<CleanResult> CleanAsync(
        IReadOnlyList<JunkFile> files,
        CleanOptions? options = null,
        IProgress<CleanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
