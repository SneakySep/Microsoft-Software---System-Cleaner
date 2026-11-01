using WindowsCleaner.Core.Models;

namespace WindowsCleaner.Core.Targets;

/// <summary>
/// Supplies the set of directories the cleaner is allowed to touch.
/// </summary>
/// <remarks>
/// Injected rather than hard-coded inside the scanner for two reasons: it is the one place
/// a user could later add their own folders, and tests can point the whole pipeline at a
/// sandbox directory instead of the real <c>%TEMP%</c>.
/// </remarks>
public interface ICleanTargetProvider
{
    IReadOnlyList<CleanTarget> GetTargets();
}
