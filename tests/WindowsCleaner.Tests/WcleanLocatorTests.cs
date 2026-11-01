using WindowsCleaner.Core.Scheduling;

namespace WindowsCleaner.Tests;

/// <summary>
/// Finding the command-line tool that a scheduled job has to run.
/// </summary>
/// <remarks>
/// The search is exercised through <see cref="WcleanLocator.TryLocateFrom"/> with the start
/// folder and depth supplied explicitly. The convenience overload searches upwards from wherever
/// the test runner happens to live, which in this repository would find a real build output and
/// make these tests depend on the machine.
/// </remarks>
public sealed class WcleanLocatorTests
{
    [Fact]
    public void Finds_the_cli_next_to_the_app()
    {
        using var sandbox = new Sandbox();
        var expected = WriteCli(sandbox.Root);

        var found = WcleanLocator.TryLocateFrom(sandbox.Root, 0, null, out var path, out _);

        Assert.True(found);
        Assert.Equal(expected, path);
    }

    [Fact]
    public void Prefers_an_explicit_override_over_the_search()
    {
        using var sandbox = new Sandbox();
        WriteCli(sandbox.Root);
        var chosen = WriteCli(Path.Combine(sandbox.Root, "elsewhere"));

        var found = WcleanLocator.TryLocateFrom(sandbox.Root, 0, chosen, out var path, out _);

        Assert.True(found);
        Assert.Equal(chosen, path);
    }

    [Fact]
    public void Falls_back_to_the_search_when_the_override_is_wrong()
    {
        using var sandbox = new Sandbox();
        var beside = WriteCli(sandbox.Root);
        var missing = Path.Combine(sandbox.Root, "gone", "wclean.exe");

        var found = WcleanLocator.TryLocateFrom(sandbox.Root, 0, missing, out var path, out var searched);

        Assert.True(found);
        Assert.Equal(beside, path);

        // The override that was tried and rejected is reported, so a bad setting is visible.
        Assert.Contains(missing, searched);
    }

    [Fact]
    public void Finds_a_published_layout_in_a_subfolder()
    {
        using var sandbox = new Sandbox();
        var expected = WriteCli(Path.Combine(sandbox.Root, "cli"));

        var found = WcleanLocator.TryLocateFrom(sandbox.Root, 0, null, out var path, out _);

        Assert.True(found);
        Assert.Equal(expected, path);
    }

    [Fact]
    public void Finds_a_source_checkout_build_output()
    {
        using var sandbox = new Sandbox();

        // The layout this repository has: the CLI project builds into its own bin folder.
        var expected = WriteCli(Path.Combine(
            sandbox.Root, "src", "WindowsCleaner.Cli", "bin", "Debug", "net10.0"));

        var found = WcleanLocator.TryLocateFrom(sandbox.Root, 0, null, out var path, out _);

        Assert.True(found);
        Assert.Equal(expected, path);
    }

    [Fact]
    public void Picks_the_newest_build_when_several_exist()
    {
        using var sandbox = new Sandbox();

        var debug = WriteCli(Path.Combine(
            sandbox.Root, "src", "WindowsCleaner.Cli", "bin", "Debug", "net10.0"));
        var release = WriteCli(Path.Combine(
            sandbox.Root, "src", "WindowsCleaner.Cli", "bin", "Release", "net10.0"));

        // A stale Release build must not beat the one that was just produced.
        File.SetLastWriteTimeUtc(release, DateTime.UtcNow.AddDays(-5));
        File.SetLastWriteTimeUtc(debug, DateTime.UtcNow);

        var found = WcleanLocator.TryLocateFrom(sandbox.Root, 0, null, out var path, out _);

        Assert.True(found);
        Assert.Equal(debug, path);
    }

    [Fact]
    public void Walks_up_into_a_parent_folder()
    {
        using var sandbox = new Sandbox();
        var expected = WriteCli(sandbox.Root);
        var nested = sandbox.CreateDirectory(Path.Combine("a", "b", "c"));

        var found = WcleanLocator.TryLocateFrom(nested, 3, null, out var path, out _);

        Assert.True(found);
        Assert.Equal(expected, path);
    }

    [Fact]
    public void Stops_walking_up_when_told_to()
    {
        using var sandbox = new Sandbox();
        WriteCli(sandbox.Root);
        var nested = sandbox.CreateDirectory("only-here");

        var found = WcleanLocator.TryLocateFrom(nested, 0, null, out var path, out var searched);

        Assert.False(found);
        Assert.Null(path);
        Assert.NotEmpty(searched);
    }

    private static string WriteCli(string directory)
    {
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, WcleanLocator.ExecutableName);
        File.WriteAllText(path, "stub");

        return path;
    }
}
