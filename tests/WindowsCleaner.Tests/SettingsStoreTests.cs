using WindowsCleaner.Core.Configuration;

namespace WindowsCleaner.Tests;

/// <summary>
/// Settings are a convenience, never a prerequisite: a missing, corrupt or unwritable file must
/// degrade to defaults rather than stop the app from starting.
/// </summary>
public sealed class SettingsStoreTests
{
    [Fact]
    public void Returns_defaults_when_no_file_exists()
    {
        using var sandbox = new Sandbox();
        var store = new SettingsStore(Path.Combine(sandbox.Root, "settings.json"));

        var settings = store.Load();

        Assert.Empty(settings.ExtraFolders);
    }

    [Fact]
    public void Round_trips_the_folder_list()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Root, "settings.json");
        var store = new SettingsStore(path);

        var saved = store.Save(new CleanerSettings { ExtraFolders = [@"D:\Cache", @"E:\Scratch"] });
        var loaded = store.Load();

        Assert.True(saved);
        Assert.Equal([@"D:\Cache", @"E:\Scratch"], loaded.ExtraFolders);
    }

    [Fact]
    public void Creates_the_containing_directory_on_first_save()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Root, "nested", "deeper", "settings.json");

        var saved = new SettingsStore(path).Save(new CleanerSettings { ExtraFolders = [@"D:\Cache"] });

        Assert.True(saved);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Falls_back_to_defaults_when_the_file_is_corrupt()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Root, "settings.json");
        File.WriteAllText(path, "{ this is not valid json");

        var settings = new SettingsStore(path).Load();

        Assert.Empty(settings.ExtraFolders);
    }

    [Fact]
    public void Falls_back_to_defaults_when_the_file_is_empty()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Root, "settings.json");
        File.WriteAllText(path, string.Empty);

        var settings = new SettingsStore(path).Load();

        Assert.Empty(settings.ExtraFolders);
    }

    [Fact]
    public void Reports_failure_instead_of_throwing_when_it_cannot_write()
    {
        using var sandbox = new Sandbox();

        // A file where the directory needs to be makes the write impossible.
        var blocker = Path.Combine(sandbox.Root, "blocker");
        File.WriteAllText(blocker, "not a directory");

        var saved = new SettingsStore(Path.Combine(blocker, "settings.json"))
            .Save(new CleanerSettings { ExtraFolders = [@"D:\Cache"] });

        Assert.False(saved);
    }

    [Fact]
    public void Round_trips_the_threshold_and_the_remembered_cli_path()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Root, "settings.json");
        var store = new SettingsStore(path);

        store.Save(new CleanerSettings
        {
            ExtraFolders = [@"D:\Cache"],
            RecycleBinEmptyThresholdBytes = 5L * 1024 * 1024 * 1024,
            CliExecutablePath = @"D:\tools\wclean.exe",
        });

        var loaded = store.Load();

        // All three fields survive together. Everything that writes settings loads and re-saves
        // the whole object, so a field being dropped here would silently reset a user's choice.
        Assert.Equal([@"D:\Cache"], loaded.ExtraFolders);
        Assert.Equal(5L * 1024 * 1024 * 1024, loaded.RecycleBinEmptyThresholdBytes);
        Assert.Equal(@"D:\tools\wclean.exe", loaded.CliExecutablePath);
    }

    [Fact]
    public void Offers_emptying_the_recycle_bin_at_one_gigabyte_by_default()
    {
        using var sandbox = new Sandbox();

        var settings = new SettingsStore(Path.Combine(sandbox.Root, "settings.json")).Load();

        Assert.Equal(1024L * 1024 * 1024, settings.RecycleBinEmptyThresholdBytes);
        Assert.Null(settings.CliExecutablePath);
    }

    [Fact]
    public void Defaults_to_roaming_app_data()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowsCleaner",
            "settings.json");

        Assert.Equal(expected, SettingsStore.DefaultFilePath);
    }
}
