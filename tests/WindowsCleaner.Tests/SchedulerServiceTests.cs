using System.Runtime.Versioning;
using WindowsCleaner.Core.Scheduling;
using WindowsCleaner.Core.Services;

namespace WindowsCleaner.Tests;

/// <summary>
/// The guard rails around registering a scheduled job.
/// </summary>
/// <remarks>
/// Only the refusals are tested. Every case that succeeds would go on to create a real Task
/// Scheduler job on the machine running the tests, which a unit test has no business doing; the
/// round trip through <c>schtasks</c> is exercised by the CLI and the app's dialog instead.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SchedulerServiceTests
{
    [Fact]
    public void Refuses_to_schedule_the_desktop_app()
    {
        using var sandbox = new Sandbox();

        var result = new SchedulerService().Enable(new ScheduledCleanOptions
        {
            ExecutablePath = Path.Combine(sandbox.Root, "WindowsCleaner.exe"),
        });

        // This is the mistake the check exists for: the GUI does not implement 'clean', so the
        // job would run every night, do nothing, and never say so.
        Assert.False(result.Succeeded);
        Assert.Contains("desktop app", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refuses_a_target_that_does_not_exist()
    {
        using var sandbox = new Sandbox();

        var result = new SchedulerService().Enable(new ScheduledCleanOptions
        {
            ExecutablePath = Path.Combine(sandbox.Root, "gone", "wclean.exe"),
        });

        Assert.False(result.Succeeded);
        Assert.Contains("does not exist", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
