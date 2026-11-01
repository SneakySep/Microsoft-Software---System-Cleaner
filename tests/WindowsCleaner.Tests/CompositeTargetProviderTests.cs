using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Safety;
using WindowsCleaner.Core.Targets;

namespace WindowsCleaner.Tests;

/// <summary>Merging behaviour for the standard locations plus user-added folders.</summary>
public sealed class CompositeTargetProviderTests
{
    private static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private sealed class StubProvider(params string[] paths) : ICleanTargetProvider
    {
        public IReadOnlyList<CleanTarget> GetTargets() => paths
            .Select(p => new CleanTarget { DisplayName = "Stub", Path = PathGuard.NormalizeRoot(p) })
            .ToList();
    }

    [Fact]
    public void Keeps_the_standard_locations_when_user_folders_are_added()
    {
        using var sandbox = new Sandbox();
        var extra = sandbox.CreateDirectory("scratch");

        var provider = new CompositeTargetProvider(new StubProvider(sandbox.Root), [extra]);

        var targets = provider.GetTargets();

        // Additive, never a replacement: adding a folder must not stop %TEMP% being cleaned.
        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t => t.Path == PathGuard.NormalizeRoot(sandbox.Root));
        Assert.Contains(targets, t => t.Path == PathGuard.NormalizeRoot(extra));
    }

    [Fact]
    public void Marks_user_added_folders_so_reports_can_distinguish_them()
    {
        using var sandbox = new Sandbox();
        var extra = sandbox.CreateDirectory("scratch");

        var provider = new CompositeTargetProvider(new StubProvider(sandbox.Root), [extra]);
        var targets = provider.GetTargets();

        Assert.True(targets.Single(t => t.Path == PathGuard.NormalizeRoot(extra)).IsUserAdded);
        Assert.False(targets.Single(t => t.Path == PathGuard.NormalizeRoot(sandbox.Root)).IsUserAdded);
    }

    [Fact]
    public void Drops_a_user_folder_that_duplicates_a_standard_one()
    {
        using var sandbox = new Sandbox();

        var provider = new CompositeTargetProvider(new StubProvider(sandbox.Root), [sandbox.Root]);
        var targets = provider.GetTargets();

        Assert.Single(targets);
    }

    [Fact]
    public void Refuses_a_dangerous_folder_and_reports_why()
    {
        using var sandbox = new Sandbox();

        // A settings file is editable, so the guard is re-applied here rather than trusted.
        var provider = new CompositeTargetProvider(new StubProvider(sandbox.Root), [Windows]);
        var targets = provider.GetTargets();

        Assert.Single(targets);
        var rejected = Assert.Single(provider.RejectedFolders);
        Assert.Equal(Windows, rejected.Path);
        Assert.NotEmpty(rejected.Reason);
    }

    [Fact]
    public void Re_reads_the_folder_list_so_the_ui_can_change_it_in_place()
    {
        using var sandbox = new Sandbox();
        var first = sandbox.CreateDirectory("first");
        var second = sandbox.CreateDirectory("second");

        var provider = new CompositeTargetProvider(new StubProvider(sandbox.Root), [first]);
        Assert.Equal(2, provider.GetTargets().Count);

        // The scanner and cleaner hold this provider by reference, so a change here has to be
        // visible without rebuilding them.
        provider.ExtraFolders = [second];

        var targets = provider.GetTargets();
        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t => t.Path == PathGuard.NormalizeRoot(second));
        Assert.DoesNotContain(targets, t => t.Path == PathGuard.NormalizeRoot(first));
    }

    [Fact]
    public void Works_with_no_user_folders_at_all()
    {
        using var sandbox = new Sandbox();

        var provider = new CompositeTargetProvider(new StubProvider(sandbox.Root), null);

        Assert.Single(provider.GetTargets());
        Assert.Empty(provider.RejectedFolders);
    }
}
