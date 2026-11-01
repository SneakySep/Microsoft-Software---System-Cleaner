using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WindowsCleaner.Core.Models;

namespace WindowsCleaner.Core.Safety;

/// <summary>
/// Asks the shell how much the Recycle Bin currently holds.
/// </summary>
/// <remarks>
/// Exists mainly so the "did recycling actually happen" question has a factual answer rather
/// than an assumption: the bin's item count and size are observable, so a test can prove a file
/// landed there instead of merely observing that it left its original location (which a
/// permanent delete would also produce).
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class RecycleBinInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    /// <summary>
    /// Totals across every volume. Returns a zeroed result if the shell declines to answer.
    /// </summary>
    public static RecycleBinStats Query()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };

        // A null root path means "all drives".
        var result = SHQueryRecycleBin(null, ref info);

        return result == 0
            ? new RecycleBinStats(info.i64Size, info.i64NumItems)
            : RecycleBinStats.Empty;
    }
}
