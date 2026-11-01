using WindowsCleaner.Core.Models;

namespace WindowsCleaner.Core.Services;

/// <summary>Discovers disposable files under the allowlisted cleaning roots.</summary>
public interface IScannerService
{
    /// <summary>
    /// Walks every configured target and returns what could be cleaned.
    /// </summary>
    /// <remarks>
    /// Never throws for ordinary file system conditions such as a locked file, a denied
    /// directory or a path that vanished mid-walk. Those are recorded in
    /// <see cref="ScanResult.Skipped"/>. It <em>does</em> throw
    /// <see cref="OperationCanceledException"/> so the UI can cancel a long scan.
    /// </remarks>
    Task<ScanResult> ScanAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
