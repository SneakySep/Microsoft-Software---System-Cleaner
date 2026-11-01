namespace WindowsCleaner.Core.Models;

/// <summary>How a cleaned file is disposed of.</summary>
public enum DeletionMode
{
    /// <summary>Delete the file outright.</summary>
    Permanent,

    /// <summary>
    /// Move the file to the Recycle Bin so the operation can be undone.
    /// </summary>
    /// <remarks>
    /// Worth being precise about the trade-off: recycling does <em>not</em> reclaim disk space
    /// until the bin is emptied, and it costs a shell operation per batch. That is why
    /// <see cref="CleanResult.FreedBytes"/> stays zero in this mode and the bytes show up in
    /// <see cref="CleanResult.RecycledBytes"/> instead.
    /// </remarks>
    RecycleBin,
}
