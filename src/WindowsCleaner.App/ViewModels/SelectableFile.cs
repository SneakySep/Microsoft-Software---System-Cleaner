using WindowsCleaner.Core.Models;

namespace WindowsCleaner.App.ViewModels;

/// <summary>
/// One row in the results list: a scanned <see cref="JunkFile"/> plus whether the user has
/// ticked it for cleaning.
/// </summary>
/// <remarks>
/// This wrapper lives in the UI layer on purpose. <see cref="JunkFile"/> stays an immutable
/// snapshot in Core, so the engine never has to reason about mutable selection state, and the
/// checkbox binding gets the change notification <see cref="ObservableObject"/> provides.
/// </remarks>
public sealed class SelectableFile : ObservableObject
{
    private bool _isSelected;

    public SelectableFile(JunkFile file)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));

        // Opt-out rather than opt-in: the expected action is to clean what the scan found, and
        // the checkbox exists to let the user carve out exceptions.
        _isSelected = true;
    }

    public JunkFile File { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string FileName => File.FileName;

    public string FullPath => File.FullPath;

    public long SizeBytes => File.SizeBytes;

    public DateTime LastWriteTimeUtc => File.LastWriteTimeUtc;
}
