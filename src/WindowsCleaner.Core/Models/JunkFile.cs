namespace WindowsCleaner.Core.Models;

/// <summary>
/// A single disposable file discovered by the scanner.
/// </summary>
/// <remarks>
/// The size and timestamp are captured during the scan so the UI can sort and display the
/// list without touching the file system again. They are therefore a snapshot: a file may
/// change between the scan and the clean. The cleaner re-reads the file before deleting it
/// and reports any mismatch rather than trusting these values.
/// </remarks>
public sealed class JunkFile
{
    /// <summary>Absolute, normalized path of the file.</summary>
    public required string FullPath { get; init; }

    /// <summary>Size in bytes at the time of the scan.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Last write time (UTC) at the time of the scan.</summary>
    public required DateTime LastWriteTimeUtc { get; init; }

    /// <summary>Name of the <see cref="CleanTarget"/> this file came from.</summary>
    public required string TargetName { get; init; }

    /// <summary>File name including extension, for display.</summary>
    public string FileName => System.IO.Path.GetFileName(FullPath);
}
