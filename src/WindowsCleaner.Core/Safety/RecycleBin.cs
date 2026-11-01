using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WindowsCleaner.Core.Safety;

/// <summary>
/// Sends files to the Windows Recycle Bin via the shell, so the operation can be undone.
/// </summary>
/// <remarks>
/// <para>
/// There is no managed API for recycling a file, so this wraps <c>SHFileOperation</c>. The
/// alternative, the COM <c>IFileOperation</c> interface, is the more modern shell API but
/// requires hand-written vtable-accurate interop for several interfaces; <c>SHFileOperation</c>
/// is stable, documented, and sufficient here.
/// </para>
/// <para><b>Two limitations worth knowing.</b> First, <c>SHFileOperation</c> predates long-path
/// support and does not understand the <c>\\?\</c> prefix, so over-long paths are refused up
/// front (see <see cref="IsSupported(string)"/>) rather than silently mishandled. Second, if the
/// target volume has no Recycle Bin available, the shell falls back to deleting permanently
/// despite <c>FOF_ALLOWUNDO</c>; that is shell behaviour this wrapper cannot override.</para>
/// <para>Each call also has to restore the process working directory, because
/// <c>SHFileOperation</c> is documented to change it.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;

    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    /// <summary>
    /// <c>SHFileOperation</c> is limited to the classic MAX_PATH and rejects the extended
    /// path prefix, so anything longer is not something it can be trusted with.
    /// </summary>
    private const int MaxShellPath = 259;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;

        /// <summary>Double-null-terminated list of source paths.</summary>
        public string pFrom;

        public string? pTo;
        public ushort fFlags;

        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;

        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>
    /// True when this path can be handed to the shell.
    /// </summary>
    /// <remarks>
    /// The extended <c>\\?\</c> prefix must be stripped from any verbatim path before it reaches
    /// the shell, so those are treated as unsupported here too.
    /// </remarks>
    public static bool IsSupported(string path) =>
        !string.IsNullOrEmpty(path)
        && path.Length <= MaxShellPath
        && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
        && !path.StartsWith(@"\\.\", StringComparison.Ordinal);

    /// <summary>
    /// Sends every path in the batch to the Recycle Bin.
    /// </summary>
    /// <returns>
    /// True only when the shell reports that the whole batch succeeded and nothing was aborted.
    /// On false, <paramref name="error"/> explains why, and the caller is expected to retry the
    /// batch file-by-file so the failure can be attributed.
    /// </returns>
    public static bool TryRecycleBatch(IReadOnlyList<string> paths, out string? error)
    {
        error = null;

        if (paths.Count == 0)
        {
            return true;
        }

        // The marshaler appends one null terminator, so a trailing '\0' in the managed string
        // is what produces the double-null termination the shell requires.
        var buffer = string.Join('\0', paths) + '\0';

        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = buffer,
            pTo = null,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };

        // SHFileOperation is documented to change the process working directory.
        var originalDirectory = TryGetCurrentDirectory();

        int result;
        try
        {
            result = SHFileOperation(ref operation);
        }
        finally
        {
            RestoreCurrentDirectory(originalDirectory);
        }

        if (result != 0)
        {
            error = DescribeShellError(result);
            return false;
        }

        if (operation.fAnyOperationsAborted)
        {
            error = "The shell aborted the operation";
            return false;
        }

        return true;
    }

    private static string? TryGetCurrentDirectory()
    {
        try
        {
            return Directory.GetCurrentDirectory();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void RestoreCurrentDirectory(string? directory)
    {
        if (directory is null)
        {
            return;
        }

        try
        {
            Directory.SetCurrentDirectory(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Best effort only; failing to restore the working directory must not fail the clean.
        }
    }

    private static string DescribeShellError(int code) => code switch
    {
        0x71 => "The Recycle Bin is disabled or unavailable for this location",
        0x7C => "The path is invalid",
        0x78 => "Access denied by the shell",
        0x20 => "Sharing violation",
        0x5A => "Refused by the shell",
        _ => $"Shell error 0x{code:X}",
    };
}
