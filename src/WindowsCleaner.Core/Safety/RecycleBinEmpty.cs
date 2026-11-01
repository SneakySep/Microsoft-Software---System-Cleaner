using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WindowsCleaner.Core.Safety;

/// <summary>
/// Empties the Windows Recycle Bin, permanently destroying everything in it.
/// </summary>
/// <remarks>
/// <para>
/// There is no managed API for this either, so the shell's <c>SHEmptyRecycleBin</c> is used. It
/// takes no list of files: the whole bin, or the whole bin on one volume, is all it can express.
/// That is exactly why the app asks for confirmation before calling it.
/// </para>
/// <para>
/// The shell's own confirmation, progress bar and sound are all suppressed, so the user's
/// consent has to come from the calling app. Leaving the shell's confirmation on as well would
/// mean two prompts for one irreversible action.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class RecycleBinEmpty
{
    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    /// <summary>
    /// Empties the bin across every volume.
    /// </summary>
    /// <returns>False with <paramref name="error"/> set when the shell refused.</returns>
    public static bool TryEmpty(out string? error)
    {
        error = null;

        // A null window handle is fine: nothing here needs a parent window, and the flags above
        // suppress any UI the shell would otherwise show.
        var result = SHEmptyRecycleBin(
            IntPtr.Zero,
            null,
            SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);

        if (result == 0)
        {
            return true;
        }

        error = Describe(result);
        return false;
    }

    private static string Describe(int hresult) => hresult switch
    {
        // Documented quirk: the shell reports this when there is nothing to empty. Callers avoid
        // reaching this path by checking the bin first, but it is worth naming rather than
        // showing the user a raw HRESULT.
        unchecked((int)0x8000FFFF) => "The shell reported an unexpected error (the Recycle Bin may already be empty)",

        unchecked((int)0x80070005) => "Access denied by the shell",

        _ => $"Shell error 0x{(uint)hresult:X8}",
    };
}
