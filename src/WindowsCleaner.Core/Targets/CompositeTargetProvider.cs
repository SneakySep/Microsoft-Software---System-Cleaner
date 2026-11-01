using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Safety;

namespace WindowsCleaner.Core.Targets;

/// <summary>
/// Merges the standard Windows locations with the folders the user has added.
/// </summary>
/// <remarks>
/// Additive by design: a user folder never replaces the built-in set, so adding one cannot
/// silently stop <c>%TEMP%</c> from being cleaned. Every user folder is re-screened through
/// <see cref="SafeRootValidator"/> here rather than trusting whatever produced the settings
/// file, because that file is editable and may predate the current rules.
/// </remarks>
public sealed class CompositeTargetProvider : ICleanTargetProvider
{
    private readonly ICleanTargetProvider _baseProvider;

    public CompositeTargetProvider(ICleanTargetProvider baseProvider, IEnumerable<string>? extraFolders)
    {
        _baseProvider = baseProvider ?? throw new ArgumentNullException(nameof(baseProvider));
        ExtraFolders = extraFolders?.ToList() ?? [];
    }

    /// <summary>
    /// The user folders to merge in.
    /// </summary>
    /// <remarks>
    /// Settable so the UI can change the set without rebuilding the scanner and cleaner, which
    /// both hold this provider by reference. The next scan or clean picks up the new value.
    /// </remarks>
    public IReadOnlyList<string> ExtraFolders { get; set; }

    /// <summary>Folders that were rejected by the guard rail, with the reason, for reporting.</summary>
    public IReadOnlyList<(string Path, string Reason)> RejectedFolders { get; private set; } = [];

    public IReadOnlyList<CleanTarget> GetTargets()
    {
        var targets = _baseProvider.GetTargets().ToList();
        var rejected = new List<(string, string)>();

        // Seed with the standard locations so a user folder can never duplicate one.
        var seen = new HashSet<string>(
            targets.Select(t => t.Path),
            StringComparer.OrdinalIgnoreCase);

        foreach (var folder in ExtraFolders)
        {
            if (!SafeRootValidator.IsAllowed(folder, out var reason))
            {
                rejected.Add((folder, reason ?? "Refused"));
                continue;
            }

            var normalized = PathGuard.NormalizeRoot(folder);
            if (!seen.Add(normalized))
            {
                continue;
            }

            targets.Add(new CleanTarget
            {
                DisplayName = DescribeFolder(normalized),
                Path = normalized,
                RequiresElevation = false,
                IsUserAdded = true,
            });
        }

        RejectedFolders = rejected;
        return targets;
    }

    private static string DescribeFolder(string path)
    {
        // Use the leaf name, falling back to the full path for a root such as "D:\".
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
