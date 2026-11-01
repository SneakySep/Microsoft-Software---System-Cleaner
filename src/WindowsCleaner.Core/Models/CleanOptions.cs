namespace WindowsCleaner.Core.Models;

/// <summary>Options for a clean run.</summary>
public sealed class CleanOptions
{
    /// <summary>
    /// Whether to delete permanently or move files to the Recycle Bin.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="DeletionMode.RecycleBin"/>. For a tool whose entire purpose is
    /// removing files the user has not inspected one by one, undo is the safer default, and a
    /// caller that genuinely wants the space back immediately can say so explicitly.
    /// </remarks>
    public DeletionMode Mode { get; init; } = DeletionMode.RecycleBin;

    /// <summary>
    /// Files per Recycle Bin operation.
    /// </summary>
    /// <remarks>
    /// Only used in <see cref="DeletionMode.RecycleBin"/>. Recycling goes through the shell,
    /// which is far slower per call than <c>File.Delete</c>, so files are submitted in batches.
    /// A batch that fails is then retried one file at a time so the failure can be attributed
    /// to the specific file that caused it.
    /// </remarks>
    public int RecycleBatchSize { get; init; } = 64;

    public static CleanOptions Default { get; } = new();
}
