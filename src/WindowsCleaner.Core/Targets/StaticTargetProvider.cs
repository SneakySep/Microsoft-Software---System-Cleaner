using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Safety;

namespace WindowsCleaner.Core.Targets;

/// <summary>
/// Cleans an explicitly supplied set of directories instead of the standard Windows ones.
/// </summary>
/// <remarks>
/// Used by the CLI's <c>--target</c> option and by tests. Because this lets a caller direct the
/// cleaner at an arbitrary directory, the resulting paths are still normalized here and the
/// cleaner still runs every <see cref="PathGuard"/> check against them, so the allowlist stays
/// meaningful: the roots change, but nothing can escape whatever roots are configured.
/// </remarks>
public sealed class StaticTargetProvider(IEnumerable<string> directories) : ICleanTargetProvider
{
    private readonly IReadOnlyList<string> _directories =
        directories?.ToList() ?? throw new ArgumentNullException(nameof(directories));

    public IReadOnlyList<CleanTarget> GetTargets() =>
        _directories
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d =>
            {
                var full = Path.GetFullPath(d);
                return new CleanTarget
                {
                    DisplayName = Path.GetFileName(Path.TrimEndingDirectorySeparator(full)) is { Length: > 0 } name
                        ? name
                        : full,
                    Path = PathGuard.NormalizeRoot(full),
                    RequiresElevation = false,
                };
            })
            .ToList();
}
