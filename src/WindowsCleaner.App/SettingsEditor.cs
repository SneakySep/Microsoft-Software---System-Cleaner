using WindowsCleaner.Core.Configuration;

namespace WindowsCleaner.App;

/// <summary>
/// Applies a change to the saved settings without discarding the values it does not mention.
/// </summary>
/// <remarks>
/// <see cref="SettingsStore.Load"/> and <see cref="SettingsStore.Save"/> replace the whole file,
/// so writing a freshly constructed <c>CleanerSettings</c> to change one field would silently
/// reset every other one. Everything that touches settings goes through here so that cannot
/// happen by accident.
/// </remarks>
internal static class SettingsEditor
{
    public static void Update(SettingsStore store, Action<CleanerSettings> change)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(change);

        var settings = store.Load();
        change(settings);
        store.Save(settings);
    }
}
