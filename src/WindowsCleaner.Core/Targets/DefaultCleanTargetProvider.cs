using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Safety;

namespace WindowsCleaner.Core.Targets;

/// <summary>
/// The standard Windows temporary locations.
/// </summary>
/// <remarks>
/// This list is intentionally short and conservative. Every entry is a directory Windows
/// itself treats as disposable, so emptying it cannot break an installed application.
/// Categories that are commonly offered by commercial cleaners but are deliberately
/// excluded, because they need more care than a file sweep:
/// <list type="bullet">
///   <item><description>Windows Update download cache (needs the update services stopped first).</description></item>
///   <item><description>Prefetch (deleting it degrades boot and app launch times).</description></item>
///   <item><description>Browser caches (belong to a profile; deleting under a running browser is unsafe).</description></item>
/// </list>
/// </remarks>
public sealed class DefaultCleanTargetProvider : ICleanTargetProvider
{
    public IReadOnlyList<CleanTarget> GetTargets()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userTemp = Path.GetTempPath();
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var targets = new List<CleanTarget>
        {
            new()
            {
                DisplayName = "User temporary files",
                Path = userTemp,
                RequiresElevation = false,
            },
            new()
            {
                DisplayName = "Windows temporary files",
                Path = Path.Combine(windowsDir, "Temp"),
                RequiresElevation = true,
            },
            new()
            {
                DisplayName = "Windows crash dumps",
                Path = Path.Combine(localAppData, "CrashDumps"),
                RequiresElevation = false,
            },
            new()
            {
                DisplayName = "Internet cache",
                Path = Path.Combine(localAppData, "Microsoft", "Windows", "INetCache"),
                RequiresElevation = false,
            },
        };

        // Normalize once here so the rest of the pipeline can rely on canonical roots,
        // and drop anything the environment did not resolve to a real absolute path.
        return targets
            .Where(t => !string.IsNullOrWhiteSpace(t.Path) && Path.IsPathFullyQualified(t.Path))
            .Select(t => new CleanTarget
            {
                DisplayName = t.DisplayName,
                Path = PathGuard.NormalizeRoot(t.Path),
                RequiresElevation = t.RequiresElevation,
            })
            .ToList();
    }
}
