using System.Text.Json;

namespace WindowsCleaner.Core.Configuration;

/// <summary>
/// Loads and saves <see cref="CleanerSettings"/> as JSON under <c>%APPDATA%</c>.
/// </summary>
/// <remarks>
/// Every operation is defensive. Settings are a convenience, never a prerequisite: a missing,
/// corrupt or unwritable file degrades to defaults rather than stopping the app from starting.
/// The file lives in roaming AppData so the folder list follows a roaming profile.
/// </remarks>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SettingsStore(string? filePath = null) => FilePath = filePath ?? DefaultFilePath;

    /// <summary>The default location: <c>%APPDATA%\WindowsCleaner\settings.json</c>.</summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WindowsCleaner",
        "settings.json");

    public string FilePath { get; }

    /// <summary>Reads the settings, falling back to defaults on any problem.</summary>
    public CleanerSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new CleanerSettings();
            }

            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<CleanerSettings>(json, SerializerOptions);

            return settings ?? new CleanerSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new CleanerSettings();
        }
    }

    /// <summary>Writes the settings, returning false rather than throwing if it cannot.</summary>
    public bool Save(CleanerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, SerializerOptions));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
