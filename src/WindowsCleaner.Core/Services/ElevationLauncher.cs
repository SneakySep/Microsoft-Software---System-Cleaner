using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace WindowsCleaner.Core.Services;

/// <summary>The outcome of asking Windows to restart with administrator rights.</summary>
public sealed record ElevationResult(bool Started, string Message);

/// <summary>
/// Asks Windows to start a fresh instance of a program with administrator rights.
/// </summary>
/// <remarks>
/// <para>
/// Relaunching is how the app offers elevation. The alternative - a privileged helper process
/// doing the deleting on behalf of the unelevated one - would mean an elevated process acting on
/// a file list chosen by an unelevated one, which is a trust boundary worth a lot of care and not
/// worth crossing for a cleaner. Restarting keeps one process, one set of rules, and the prompt
/// in Windows' own hands.
/// </para>
/// <para>
/// The consent prompt belongs to Windows, on its own secure desktop: nothing here can or should
/// click it, so declining is a normal outcome rather than an error.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ElevationLauncher
{
    /// <summary>The shell verb that triggers the consent prompt.</summary>
    public const string ElevateVerb = "runas";

    /// <summary>Windows' "the user declined the elevation prompt" error.</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// Starts <paramref name="executablePath"/> elevated, returning as soon as it has been
    /// launched.
    /// </summary>
    public static ElevationResult TryRelaunchElevated(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new ElevationResult(false, "Could not work out which program to restart.");
        }

        try
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                // ShellExecute is required for a verb to be applied at all, and "runas" is what
                // makes the shell go through the consent prompt.
                UseShellExecute = true,
                Verb = ElevateVerb,
            };

            var directory = Path.GetDirectoryName(executablePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                startInfo.WorkingDirectory = directory;
            }

            using var process = Process.Start(startInfo);

            return process is null
                ? new ElevationResult(false, "Windows did not start the elevated copy.")
                : new ElevationResult(true, "Restarted with administrator rights.");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return new ElevationResult(false, "Elevation was declined, so nothing changed.");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or FileNotFoundException or PlatformNotSupportedException)
        {
            return new ElevationResult(false, $"Could not restart as administrator: {e.Message}");
        }
    }
}
