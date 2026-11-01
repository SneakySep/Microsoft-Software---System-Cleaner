using System.Runtime.Versioning;
using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Safety;

namespace WindowsCleaner.Core.Services;

/// <inheritdoc cref="IRecycleBinService"/>
[SupportedOSPlatform("windows")]
public sealed class RecycleBinService : IRecycleBinService
{
    public RecycleBinStats Query() => RecycleBinInfo.Query();

    public Task<RecycleBinEmptyResult> EmptyAsync(CancellationToken cancellationToken = default) =>
        Task.Run(EmptyCore, cancellationToken);

    private static RecycleBinEmptyResult EmptyCore()
    {
        var before = RecycleBinInfo.Query();

        // Returned before touching the shell for two reasons: there is nothing to destroy, and
        // some Windows versions answer E_UNEXPECTED when asked to empty a bin that is already
        // empty, which would otherwise be reported to the user as a failure.
        if (before.IsEmpty)
        {
            return new RecycleBinEmptyResult(true, 0, 0, before, before, null);
        }

        if (!RecycleBinEmpty.TryEmpty(out var error))
        {
            return RecycleBinEmptyResult.Failure(before, error ?? "The Recycle Bin could not be emptied.");
        }

        // Measured, not assumed. The shell is asked again afterwards, so the reported figures are
        // what actually left the bin; a file held open by another process survives even this, and
        // claiming its bytes as reclaimed would overstate what happened.
        var after = RecycleBinInfo.Query();

        return new RecycleBinEmptyResult(
            true,
            Math.Max(0, before.SizeBytes - after.SizeBytes),
            Math.Max(0, before.ItemCount - after.ItemCount),
            before,
            after,
            null);
    }
}
