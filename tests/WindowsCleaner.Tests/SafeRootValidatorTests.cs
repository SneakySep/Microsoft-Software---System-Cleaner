using WindowsCleaner.Core.Targets;

namespace WindowsCleaner.Tests;

/// <summary>
/// Adding your own folders to a deleting tool is the highest-risk feature it has, so the guard
/// rail that screens those folders is tested against the specific locations whose loss would be
/// catastrophic.
/// </summary>
public sealed class SafeRootValidatorTests
{
    private static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void Rejects_a_whole_drive()
    {
        Assert.False(SafeRootValidator.IsAllowed(@"C:\", out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void Rejects_the_windows_directory()
    {
        Assert.False(SafeRootValidator.IsAllowed(Windows, out _));
    }

    [Fact]
    public void Rejects_system32()
    {
        Assert.False(SafeRootValidator.IsAllowed(Path.Combine(Windows, "System32"), out _));
    }

    [Fact]
    public void Rejects_the_user_profile_root()
    {
        Assert.False(SafeRootValidator.IsAllowed(Profile, out _));
    }

    [Fact]
    public void Rejects_personal_folders()
    {
        Assert.False(SafeRootValidator.IsAllowed(Path.Combine(Profile, "Documents"), out _));
        Assert.False(SafeRootValidator.IsAllowed(Path.Combine(Profile, "Desktop"), out _));
        Assert.False(SafeRootValidator.IsAllowed(Path.Combine(Profile, "Downloads"), out _));
    }

    [Fact]
    public void Rejects_the_whole_local_app_data_folder()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.False(SafeRootValidator.IsAllowed(localAppData, out _));
    }

    [Fact]
    public void Rejects_a_folder_that_merely_contains_a_protected_one()
    {
        // C:\Users is not itself in the list, but it contains the user profile.
        var users = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Profile));

        if (string.IsNullOrEmpty(users))
        {
            return;
        }

        Assert.False(SafeRootValidator.IsAllowed(users, out var reason));
        Assert.Contains("contains", reason);
    }

    /// <summary>
    /// The nuance the whole design rests on: a subfolder of a protected location is acceptable
    /// even though its parent is not, otherwise the built-in C:\Windows\Temp target would be
    /// rejected by our own guard.
    /// </summary>
    [Fact]
    public void Allows_a_subfolder_of_a_protected_location()
    {
        Assert.True(SafeRootValidator.IsAllowed(Path.Combine(Windows, "Temp"), out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void Allows_an_ordinary_folder_under_the_user_temp_directory()
    {
        var candidate = Path.Combine(Path.GetTempPath(), "my-cache");

        Assert.True(SafeRootValidator.IsAllowed(candidate, out _));
    }

    [Fact]
    public void Allows_a_folder_that_does_not_exist_yet()
    {
        // Existence is a separate concern; the guard rail is about blast radius.
        var candidate = Path.Combine(Path.GetTempPath(), "not-created-" + Guid.NewGuid().ToString("N"));

        Assert.True(SafeRootValidator.IsAllowed(candidate, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_blank_input(string? path)
    {
        Assert.False(SafeRootValidator.IsAllowed(path, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void Rejects_a_relative_path()
    {
        Assert.False(SafeRootValidator.IsAllowed(@"some\relative\path", out var reason));
        Assert.Contains("absolute", reason);
    }

    [Fact]
    public void Exposes_the_protected_root_list()
    {
        Assert.NotEmpty(SafeRootValidator.Protected);
        Assert.Contains(SafeRootValidator.Protected, p => p.Equals(Windows, StringComparison.OrdinalIgnoreCase));
    }
}
