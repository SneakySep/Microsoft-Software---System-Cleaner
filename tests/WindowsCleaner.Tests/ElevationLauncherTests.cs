using System.Runtime.Versioning;
using WindowsCleaner.Core.Services;

namespace WindowsCleaner.Tests;

/// <summary>
/// Asking Windows to restart elevated.
/// </summary>
/// <remarks>
/// Only the paths that fail before Windows is involved are exercised. A test cannot answer the
/// consent prompt - it appears on Windows' own secure desktop, which is the point of it - so
/// anything that would raise one is verified by hand instead. What is checked here is that a
/// request that cannot work says so, rather than appearing to do something.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ElevationLauncherTests
{
    [Fact]
    public void Reports_a_missing_program_rather_than_asking_for_consent()
    {
        using var sandbox = new Sandbox();

        var result = ElevationLauncher.TryRelaunchElevated(Path.Combine(sandbox.Root, "not-here.exe"));

        Assert.False(result.Started);
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public void Refuses_an_empty_path()
    {
        var result = ElevationLauncher.TryRelaunchElevated("   ");

        Assert.False(result.Started);
        Assert.Contains("restart", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
