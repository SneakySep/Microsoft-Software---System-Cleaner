using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Services;

namespace WindowsCleaner.App;

/// <summary>
/// What the dashboard's view model needs from the window it lives in.
/// </summary>
/// <remarks>
/// Supplied by the composition root rather than reached for directly, so the view model has no
/// knowledge of dialogs or of the lifetime of the process it runs in. That is what lets the
/// decisions it makes - whether to destroy the Recycle Bin's contents, whether to restart - be
/// read from the view model instead of hunted for in a window.
/// </remarks>
public sealed record MainWindowActions(
    Func<RecycleBinStats, bool> ConfirmEmptyRecycleBin,
    Func<ElevationResult> RequestElevation);
