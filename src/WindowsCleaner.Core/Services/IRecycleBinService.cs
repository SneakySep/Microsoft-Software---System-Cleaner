using WindowsCleaner.Core.Models;

namespace WindowsCleaner.Core.Services;

/// <summary>Reports how much the Windows Recycle Bin is holding.</summary>
/// <remarks>
/// An interface rather than a direct call so the UI never has to touch shell interop, and so a
/// view model can be exercised without querying the real Recycle Bin.
/// </remarks>
public interface IRecycleBinService
{
    RecycleBinStats Query();

    /// <summary>
    /// Empties the Recycle Bin, permanently destroying everything in it.
    /// </summary>
    /// <remarks>
    /// Irreversible, and it cannot be scoped to particular files: the shell only offers "all of
    /// it" or "all of it on this volume". Callers are expected to confirm with the user first,
    /// stating exactly what is about to be destroyed.
    /// </remarks>
    Task<RecycleBinEmptyResult> EmptyAsync(CancellationToken cancellationToken = default);
}
