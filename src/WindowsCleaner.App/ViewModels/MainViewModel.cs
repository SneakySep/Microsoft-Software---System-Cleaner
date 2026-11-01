using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using Microsoft.Win32;
using WindowsCleaner.Core.Configuration;
using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Safety;
using WindowsCleaner.Core.Services;
using WindowsCleaner.Core.Targets;
using WindowsCleaner.Core.Utilities;

namespace WindowsCleaner.App.ViewModels;

/// <summary>
/// Drives the dashboard: scan, present selectable results, clean what is ticked, and report
/// what happened.
/// </summary>
/// <remarks>
/// <para>This class contains no file system code of its own. It talks to
/// <see cref="IScannerService"/>, <see cref="ICleanerService"/> and
/// <see cref="IRecycleBinService"/>, which is what allows the engine to be tested without a UI
/// and the UI to be swapped without touching the engine.</para>
///
/// <para>Every operation is awaited and reports progress through <see cref="IProgress{T}"/>,
/// so the dispatcher thread is never blocked and the window stays responsive throughout.</para>
/// </remarks>
public sealed class MainViewModel : ObservableObject
{
    private readonly IScannerService _scanner;
    private readonly ICleanerService _cleaner;
    private readonly IRecycleBinService _recycleBin;
    private readonly SettingsStore _settings;
    private readonly CompositeTargetProvider _targetProvider;
    private readonly CleanReportStore _reports;

    /// <summary>
    /// The parts of this view model that need a window: confirmations, and restarting the app.
    /// </summary>
    private readonly MainWindowActions _actions;

    /// <summary>
    /// How much the Recycle Bin must hold before emptying it is offered.
    /// </summary>
    /// <remarks>
    /// Emptying destroys the whole bin irreversibly and cannot be scoped to particular files, so
    /// it is not something to put one click away from a small bin somebody is keeping on purpose.
    /// </remarks>
    private readonly long _emptyBinThresholdBytes;

    /// <summary>How many runs the dashboard lists. The stored history keeps more than this.</summary>
    private const int MaxHistoryRows = 10;

    private bool _canEmptyRecycleBin;
    private string _emptyBinTooltip = string.Empty;
    private string? _lastCleanHeadline;
    private string _lastCleanDetails = string.Empty;
    private bool _showLastCleanDetails;
    private IReadOnlyList<HistoryRow> _history = [];
    private string _historySummary = string.Empty;
    private bool _showHistory;

    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<SelectableFile> _files = [];

    /// <summary>
    /// Guards the selection totals while a bulk change runs.
    /// </summary>
    /// <remarks>
    /// Ticking 16,000 checkboxes one at a time would otherwise trigger 16,000 recalculation
    /// passes over the whole list, which is the difference between an instant action and a
    /// multi-second freeze.
    /// </remarks>
    private bool _suppressSelectionRecalculation;

    private bool _isBusy;
    private bool _hasScanned;
    private double _progressValue;
    private string _selectedSizeText = "\u2014";
    private string _selectedCountText = "\u2014";
    private string _locationText = "\u2014";
    private string _statusText = "Ready to scan. Nothing is removed until you press Clean Now.";
    private string _currentPathText = string.Empty;
    private string _skippedText = string.Empty;
    private string _recycleBinText = string.Empty;
    private bool _showRecycleBinHint;
    private string? _bannerText;
    private bool _bannerIsPositive;
    private bool _moveToRecycleBin = true;

    public MainViewModel(
        IScannerService scanner,
        ICleanerService cleaner,
        IRecycleBinService recycleBin,
        SettingsStore settings,
        CompositeTargetProvider targetProvider,
        CleanReportStore reportStore,
        MainWindowActions actions)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _cleaner = cleaner ?? throw new ArgumentNullException(nameof(cleaner));
        _recycleBin = recycleBin ?? throw new ArgumentNullException(nameof(recycleBin));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _targetProvider = targetProvider ?? throw new ArgumentNullException(nameof(targetProvider));
        _reports = reportStore ?? throw new ArgumentNullException(nameof(reportStore));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _emptyBinThresholdBytes = settings.Load().RecycleBinEmptyThresholdBytes;

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
        CleanCommand = new AsyncRelayCommand(CleanAsync, () => !IsBusy && SelectedCount > 0);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        SelectAllCommand = new RelayCommand(() => SetAllSelected(true), () => !IsBusy && HasFiles);
        SelectNoneCommand = new RelayCommand(() => SetAllSelected(false), () => !IsBusy && HasFiles);
        AddFolderCommand = new RelayCommand(AddFolder, () => !IsBusy);
        RemoveFolderCommand = new RelayCommand<string>(RemoveFolder, _ => !IsBusy);
        OpenRecycleBinCommand = new RelayCommand(OpenRecycleBin);
        EmptyRecycleBinCommand = new AsyncRelayCommand(EmptyRecycleBinAsync, () => !IsBusy && CanEmptyRecycleBin);
        ToggleLastCleanDetailsCommand = new RelayCommand(ToggleLastCleanDetails, () => HasLastClean);
        ToggleHistoryCommand = new RelayCommand(() => ShowHistory = !ShowHistory, () => HasHistory);
        OpenSchedulerCommand = new RelayCommand(() => SchedulerRequested?.Invoke(this, EventArgs.Empty));
        RestartElevatedCommand = new RelayCommand(RestartElevated, () => IsElevated == false);

        // Cleaning C:\Windows\Temp needs elevation; warn up front rather than failing silently.
        ShowElevationNotice = !Environment.IsPrivilegedProcess;

        LoadCustomFolders();
        RefreshRecycleBin();
        RefreshHistory();
    }

    /// <summary>Raised when the user asks to open the scheduled cleaning dialog.</summary>
    /// <remarks>
    /// An event rather than a call into the window, so the view model stays testable and unaware
    /// of the view that hosts it.
    /// </remarks>
    public event EventHandler? SchedulerRequested;

    public ICommand ScanCommand { get; }
    public ICommand CleanCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RemoveFolderCommand { get; }
    public ICommand OpenRecycleBinCommand { get; }
    public ICommand EmptyRecycleBinCommand { get; }
    public ICommand ToggleLastCleanDetailsCommand { get; }
    public ICommand ToggleHistoryCommand { get; }
    public ICommand OpenSchedulerCommand { get; }
    public ICommand RestartElevatedCommand { get; }

    /// <summary>Folders the user has added on top of the standard Windows locations.</summary>
    public ObservableCollection<string> CustomFolders { get; } = [];

    /// <summary>
    /// The files found by the last scan, each with its own checkbox state.
    /// </summary>
    /// <remarks>
    /// Deliberately a plain list swapped in one assignment rather than an
    /// <c>ObservableCollection</c> that is appended to. A temp scan routinely turns up tens of
    /// thousands of entries, and raising a <c>CollectionChanged</c> per item would stall the
    /// dispatcher for seconds; one property change is enough because the list only ever
    /// changes wholesale. Rows do raise change notifications individually, for the checkboxes.
    /// </remarks>
    public IReadOnlyList<SelectableFile> Files
    {
        get => _files;
        private set
        {
            _files = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFiles));
            OnPropertyChanged(nameof(ShowEmptyState));
            RaiseCommandStates();
        }
    }

    public bool HasFiles => _files.Count > 0;

    public bool ShowEmptyState => !HasFiles && !_isBusy;

    /// <summary>Message shown in place of the list before a scan, or when a scan finds nothing.</summary>
    public string EmptyStateText => _hasScanned
        ? "Nothing to clean \u2014 no disposable files were found."
        : "Press Scan to look for disposable files in your temporary folders.";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(ShowEmptyState));
            RaiseCommandStates();
        }
    }

    public bool IsIdle => !_isBusy;

    public bool ShowElevationNotice { get; }

    public bool HasScanned
    {
        get => _hasScanned;
        private set
        {
            if (SetProperty(ref _hasScanned, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(EmptyStateText));
            }
        }
    }

    /// <summary>Progress in the 0-100 range, always determinate so the bar means something.</summary>
    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    /// <summary>Size of the currently ticked files: what a clean would actually reclaim.</summary>
    public string SelectedSizeText
    {
        get => _selectedSizeText;
        private set => SetProperty(ref _selectedSizeText, value);
    }

    public string SelectedCountText
    {
        get => _selectedCountText;
        private set => SetProperty(ref _selectedCountText, value);
    }

    public string LocationText
    {
        get => _locationText;
        private set => SetProperty(ref _locationText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CurrentPathText
    {
        get => _currentPathText;
        private set => SetProperty(ref _currentPathText, value);
    }

    public string SkippedText
    {
        get => _skippedText;
        private set => SetProperty(ref _skippedText, value);
    }

    /// <summary>
    /// Whether a clean sends files to the Recycle Bin instead of deleting them outright.
    /// </summary>
    /// <remarks>
    /// On by default: for a tool that removes files the user has not reviewed one by one, being
    /// able to undo is worth more than reclaiming the space immediately. When this is on, the
    /// reported figure is bytes <em>moved</em>, not bytes freed, and the UI says so.
    /// </remarks>
    public bool MoveToRecycleBin
    {
        get => _moveToRecycleBin;
        set => SetProperty(ref _moveToRecycleBin, value);
    }

    /// <summary>How much the Recycle Bin currently holds, so the user knows what is waiting there.</summary>
    public string RecycleBinText
    {
        get => _recycleBinText;
        private set => SetProperty(ref _recycleBinText, value);
    }

    /// <summary>True when recycled space is waiting to be reclaimed.</summary>
    public bool ShowRecycleBinHint
    {
        get => _showRecycleBinHint;
        private set => SetProperty(ref _showRecycleBinHint, value);
    }

    /// <summary>Whether the Recycle Bin is holding enough for emptying to be worth offering.</summary>
    public bool CanEmptyRecycleBin
    {
        get => _canEmptyRecycleBin;
        private set
        {
            if (SetProperty(ref _canEmptyRecycleBin, value))
            {
                (EmptyRecycleBinCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Says what emptying would destroy, or why it is not on offer.
    /// </summary>
    /// <remarks>
    /// A disabled button with no explanation is the worst version of this feature: the user
    /// either assumes the app is broken or goes to empty the bin by hand instead.
    /// </remarks>
    public string EmptyRecycleBinTooltip
    {
        get => _emptyBinTooltip;
        private set => SetProperty(ref _emptyBinTooltip, value);
    }

    public bool HasLastClean => _lastCleanHeadline is not null;

    /// <summary>One-line summary of the most recent clean, whichever front-end ran it.</summary>
    public string? LastCleanHeadline
    {
        get => _lastCleanHeadline;
        private set
        {
            if (SetProperty(ref _lastCleanHeadline, value))
            {
                OnPropertyChanged(nameof(HasLastClean));
                (ToggleLastCleanDetailsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>The before/after breakdown behind <see cref="LastCleanHeadline"/>.</summary>
    public string LastCleanDetails
    {
        get => _lastCleanDetails;
        private set => SetProperty(ref _lastCleanDetails, value);
    }

    public bool ShowLastCleanDetails
    {
        get => _showLastCleanDetails;
        private set => SetProperty(ref _showLastCleanDetails, value);
    }

    /// <summary>Recent runs, newest first, for the dashboard's history list.</summary>
    public IReadOnlyList<HistoryRow> HistoryRows
    {
        get => _history;
        private set
        {
            _history = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasHistory));
        }
    }

    public bool HasHistory => _history.Count > 0;

    /// <summary>Totals across the recorded runs: the trend a single run cannot show.</summary>
    public string HistorySummary
    {
        get => _historySummary;
        private set => SetProperty(ref _historySummary, value);
    }

    public bool ShowHistory
    {
        get => _showHistory;
        private set => SetProperty(ref _showHistory, value);
    }

    /// <summary>True when this process already has administrator rights.</summary>
    public bool IsElevated => !ShowElevationNotice;

    /// <summary>
    /// Confirms the app really is elevated, which is the one thing the restart has to get right.
    /// </summary>
    public bool ShowElevatedNotice => IsElevated;

    public bool HasCustomFolders => CustomFolders.Count > 0;

    public bool HasNoCustomFolders => CustomFolders.Count == 0;

    public string? BannerText
    {
        get => _bannerText;
        private set
        {
            if (SetProperty(ref _bannerText, value))
            {
                OnPropertyChanged(nameof(HasBanner));
            }
        }
    }

    public bool HasBanner => !string.IsNullOrEmpty(_bannerText);

    public bool BannerIsPositive
    {
        get => _bannerIsPositive;
        private set => SetProperty(ref _bannerIsPositive, value);
    }

    private int SelectedCount => _files.Count(f => f.IsSelected);

    private void RaiseCommandStates()
    {
        (ScanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CleanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SelectAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SelectNoneCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AddFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveFolderCommand as RelayCommand<string>)?.RaiseCanExecuteChanged();
        (EmptyRecycleBinCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ToggleHistoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RestartElevatedCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void Cancel()
    {
        // Cooperative cancellation: the scanner and cleaner poll this token between entries and
        // unwind cleanly, so partial results are discarded rather than half-applied.
        _cancellation?.Cancel();
    }

    // ============================== custom folders ==============================

    private void LoadCustomFolders()
    {
        foreach (var folder in _settings.Load().ExtraFolders)
        {
            CustomFolders.Add(folder);
        }

        PushFoldersToProvider();
    }

    private void PushFoldersToProvider()
    {
        _targetProvider.ExtraFolders = [.. CustomFolders];

        OnPropertyChanged(nameof(HasCustomFolders));
        OnPropertyChanged(nameof(HasNoCustomFolders));
    }

    /// <summary>
    /// Writes the folder list back without disturbing the other settings.
    /// </summary>
    /// <remarks>
    /// Loading and re-saving the whole object matters: the store replaces the file wholesale, so
    /// saving a freshly built <see cref="CleanerSettings"> to change the folders would reset the
    /// Recycle Bin threshold and the remembered CLI path along with it.
    /// </remarks>
    private void PersistCustomFolders() =>
        SettingsEditor.Update(_settings, settings => settings.ExtraFolders = [.. CustomFolders]);

    private void AddFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder to include in cleaning",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var path = dialog.FolderName;

        // A user folder becomes a cleaning root, so it goes through the same guard rail that
        // stops someone adding C:\ or their Documents folder.
        if (!SafeRootValidator.IsAllowed(path, out var reason))
        {
            BannerIsPositive = false;
            BannerText = reason;
            return;
        }

        var normalized = PathGuard.NormalizeRoot(path);

        if (CustomFolders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            StatusText = $"'{normalized}' is already in the list.";
            return;
        }

        CustomFolders.Add(normalized);
        PushFoldersToProvider();
        PersistCustomFolders();

        BannerText = null;
        StatusText = $"Added '{normalized}'. Press Scan to include it.";
    }

    private void RemoveFolder(string? path)
    {
        if (string.IsNullOrEmpty(path) || !CustomFolders.Remove(path))
        {
            return;
        }

        PushFoldersToProvider();
        PersistCustomFolders();

        StatusText = $"Removed '{path}'. Press Scan to refresh.";
    }

    // ================================ recycle bin ===============================

    private void RefreshRecycleBin()
    {
        try
        {
            var stats = _recycleBin.Query();

            RecycleBinText = stats.IsEmpty
                ? "Recycle Bin is empty"
                : $"Recycle Bin holds {ByteSizeFormatter.Format(stats.SizeBytes)}";

            // Amber only once there is enough in there to be worth acting on, so the hint keeps
            // meaning something instead of being permanently lit.
            var worthEmptying = stats.MeetsEmptyThreshold(_emptyBinThresholdBytes);
            ShowRecycleBinHint = worthEmptying;
            CanEmptyRecycleBin = worthEmptying;

            EmptyRecycleBinTooltip = stats.IsEmpty
                ? "The Recycle Bin is already empty."
                : worthEmptying
                    ? $"Permanently delete everything in the Recycle Bin "
                      + $"({stats.ItemCount:N0} items, {ByteSizeFormatter.Format(stats.SizeBytes)}). "
                      + "This cannot be undone."
                    : $"Holding {ByteSizeFormatter.Format(stats.SizeBytes)}, below the "
                      + $"{ByteSizeFormatter.Format(_emptyBinThresholdBytes)} threshold for emptying.";
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
        {
            // Not fatal: the bin readout is informational, so drop it rather than break the app.
            RecycleBinText = string.Empty;
            ShowRecycleBinHint = false;
            CanEmptyRecycleBin = false;
            EmptyRecycleBinTooltip = string.Empty;
        }
    }

    /// <summary>Reads the bin's totals, tolerating a platform that will not answer.</summary>
    private RecycleBinStats QueryRecycleBin()
    {
        try
        {
            return _recycleBin.Query();
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
        {
            return RecycleBinStats.Empty;
        }
    }

    /// <summary>
    /// Empties the Recycle Bin after asking, and reports what actually left it.
    /// </summary>
    private async Task EmptyRecycleBinAsync()
    {
        var before = QueryRecycleBin();

        if (before.IsEmpty)
        {
            StatusText = "The Recycle Bin is already empty.";
            RefreshRecycleBin();
            return;
        }

        if (!_actions.ConfirmEmptyRecycleBin(before))
        {
            StatusText = "Left the Recycle Bin untouched.";
            return;
        }

        BannerText = null;
        IsBusy = true;
        StatusText = $"Emptying the Recycle Bin ({before.ItemCount:N0} items)\u2026";

        try
        {
            var result = await _recycleBin.EmptyAsync();

            if (!result.Succeeded)
            {
                BannerIsPositive = false;
                BannerText = result.Error ?? "The Recycle Bin could not be emptied.";
                StatusText = "The Recycle Bin was not emptied.";
                return;
            }

            BannerIsPositive = result.FreedBytes > 0;
            BannerText = result.FreedBytes > 0
                ? $"Emptied the Recycle Bin: {ByteSizeFormatter.Format(result.FreedBytes)} reclaimed "
                  + $"({result.ItemsRemoved:N0} items)."
                : "The Recycle Bin is empty.";

            StatusText = "Recycle Bin emptied.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not empty the Recycle Bin: {ex.Message}";
            BannerIsPositive = false;
            BannerText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshRecycleBin();
        }
    }

    /// <summary>
    /// Restarts the app elevated so the locations that need administrator rights become cleanable.
    /// </summary>
    /// <remarks>
    /// The relaunch and the shutdown of this instance are the window's job, supplied through
    /// <see cref="MainWindowActions"/>: this view model should not be deciding when the process
    /// ends. Nothing here changes if the user declines the prompt, which is an ordinary outcome.
    /// </remarks>
    private void RestartElevated()
    {
        var result = _actions.RequestElevation();

        if (!result.Started)
        {
            StatusText = result.Message;
        }
    }

    private void ToggleLastCleanDetails() => ShowLastCleanDetails = !ShowLastCleanDetails;

    private void OpenRecycleBin()
    {
        try
        {
            // Opens the Recycle Bin in Explorer. Deliberately does not empty it: destroying
            // everything in someone's bin is not something a "reminder" button should do.
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText = "Could not open the Recycle Bin.";
        }
    }

    // ================================ selection ================================

    /// <summary>Replaces the result list, wiring up selection change tracking for the new rows.</summary>
    private void SetFiles(IEnumerable<SelectableFile> items)
    {
        foreach (var previous in _files)
        {
            previous.PropertyChanged -= OnFileSelectionChanged;
        }

        var next = items.ToList();
        foreach (var item in next)
        {
            item.PropertyChanged += OnFileSelectionChanged;
        }

        Files = next;
        RecalculateSelection();
    }

    private void OnFileSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableFile.IsSelected) && !_suppressSelectionRecalculation)
        {
            RecalculateSelection();
        }
    }

    private void SetAllSelected(bool selected)
    {
        _suppressSelectionRecalculation = true;
        try
        {
            foreach (var file in _files)
            {
                file.IsSelected = selected;
            }
        }
        finally
        {
            _suppressSelectionRecalculation = false;
        }

        RecalculateSelection();
    }

    private void RecalculateSelection()
    {
        var selected = _files.Where(f => f.IsSelected).ToList();

        SelectedSizeText = ByteSizeFormatter.Format(selected.Sum(f => f.SizeBytes));
        SelectedCountText = selected.Count.ToString("N0");

        RaiseCommandStates();
    }

    // ================================= scan =================================

    private async Task ScanAsync()
    {
        BannerText = null;
        SetFiles([]);
        HasScanned = false;
        ProgressValue = 0;
        SelectedSizeText = "\u2014";
        SelectedCountText = "\u2014";
        LocationText = "\u2014";
        SkippedText = string.Empty;
        CurrentPathText = string.Empty;
        StatusText = "Scanning temporary folders\u2026";

        _cancellation = new CancellationTokenSource();
        IsBusy = true;

        var progress = new Progress<ScanProgress>(OnScanProgress);

        try
        {
            var result = await _scanner.ScanAsync(progress, _cancellation.Token);

            SetFiles(result.Files.Select(f => new SelectableFile(f)));
            HasScanned = true;
            ProgressValue = 100;

            LocationText = $"{result.Targets.Count(t => t.Accessible)} / {result.Targets.Count}";
            SkippedText = DescribeSkipped(result);

            // A saved folder can be refused by the guard rail between runs (renamed, made a
            // system path, edited into the settings file by hand); say so rather than silently
            // dropping it from the scan.
            if (_targetProvider.RejectedFolders.Count > 0 && string.IsNullOrEmpty(SkippedText))
            {
                SkippedText = string.Join(
                    " ",
                    _targetProvider.RejectedFolders.Select(r => $"Ignored '{r.Path}': {r.Reason}"));
            }

            StatusText = result.Files.Count == 0
                ? "No disposable files found."
                : $"Found {result.Files.Count:N0} files ({ByteSizeFormatter.Format(result.TotalSizeBytes)}) " +
                  $"in {result.Duration.TotalSeconds:0.0}s. Untick anything you want to keep, then clean.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
            HasScanned = false;
        }
        catch (Exception ex)
        {
            // A scan should not be able to crash the app; surface it and stay usable.
            StatusText = $"Scan failed: {ex.Message}";
            BannerIsPositive = false;
            BannerText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            CurrentPathText = string.Empty;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    // ================================= clean ================================

    private async Task CleanAsync()
    {
        var selected = _files.Where(f => f.IsSelected).Select(f => f.File).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var recycling = MoveToRecycleBin;
        var options = new CleanOptions
        {
            Mode = recycling ? DeletionMode.RecycleBin : DeletionMode.Permanent,
        };

        BannerText = null;
        ProgressValue = 0;
        StatusText = recycling
            ? $"Moving {selected.Count:N0} files to the Recycle Bin\u2026"
            : $"Deleting {selected.Count:N0} files\u2026";

        _cancellation = new CancellationTokenSource();
        IsBusy = true;

        var progress = new Progress<CleanProgress>(OnCleanProgress);

        // Read around the clean so the report can show space that moved into the bin separately
        // from space that actually came back.
        var binBefore = QueryRecycleBin();

        try
        {
            var result = await _cleaner.CleanAsync(selected, options, progress, _cancellation.Token);

            ProgressValue = 100;
            StatusText = result.FailedCount == 0
                ? "Clean finished."
                : $"Clean finished with {result.FailedCount:N0} files skipped.";

            BannerText = DescribeOutcome(result);
            BannerIsPositive = result.RemovedCount > 0;

            // Whatever survived (locked or protected files) is reconciled back into the list so
            // the numbers on screen stay truthful.
            UpdateAfterClean(result);
            RefreshRecycleBin();
            RecordCleanReport(result, selected.Count, binBefore, QueryRecycleBin());
        }
        catch (OperationCanceledException)
        {
            StatusText = recycling
                ? "Cancelled \u2014 files already moved stay in the Recycle Bin."
                : "Cancelled \u2014 files already deleted stay deleted.";
            RefreshRecycleBin();
        }
        catch (Exception ex)
        {
            StatusText = $"Clean failed: {ex.Message}";
            BannerIsPositive = false;
            BannerText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            CurrentPathText = string.Empty;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    /// <summary>
    /// Builds the result message, keeping the distinction between bytes actually reclaimed and
    /// bytes merely moved to the Recycle Bin.
    /// </summary>
    private static string DescribeOutcome(CleanResult result)
    {
        if (result.RemovedCount == 0)
        {
            return result.FailedCount > 0
                ? "Nothing could be removed."
                : "Nothing was removed.";
        }

        var skipped = result.FailedCount > 0 ? $" {result.FailedCount:N0} skipped." : string.Empty;

        return result.Mode == DeletionMode.RecycleBin
            ? $"Moved {ByteSizeFormatter.Format(result.RecycledBytes)} ({result.RecycledCount:N0} files) " +
              $"to the Recycle Bin. Empty it to reclaim the space.{skipped}"
            : $"Freed {ByteSizeFormatter.Format(result.FreedBytes)} across {result.DeletedCount:N0} files.{skipped}";
    }

    /// <summary>
    /// Reconciles the displayed list with reality: whatever actually survived the clean
    /// (locked or protected files) is kept, and re-ticked so a retry is one click away.
    /// </summary>
    private void UpdateAfterClean(CleanResult result)
    {
        if (result.Failures.Count == 0)
        {
            SetFiles([]);
            SkippedText = string.Empty;
            return;
        }

        var failedPaths = result.Failures.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var survivors = _files.Where(f => failedPaths.Contains(f.FullPath)).ToList();

        foreach (var survivor in survivors)
        {
            survivor.IsSelected = true;
        }

        SetFiles(survivors);
        SkippedText = SummarizeFailures(result);
    }

    // ================================ progress ===============================

    private void OnScanProgress(ScanProgress p)
    {
        // Progress arrives on the UI thread already: Progress<T> captures the SynchronizationContext
        // at construction, and this instance was created on the dispatcher thread.
        //
        // Everything found so far is going to be selected, so these cards can show live totals
        // during the scan and are simply recomputed from the real selection once it finishes.
        SelectedSizeText = ByteSizeFormatter.Format(p.BytesFound);
        SelectedCountText = p.FilesFound.ToString("N0");

        if (p.TargetCount > 0)
        {
            ProgressValue = Math.Clamp(p.TargetIndex / (double)p.TargetCount * 100, 0, 100);
        }

        StatusText = $"Scanning: {p.TargetName}\u2026";
        CurrentPathText = p.CurrentPath ?? string.Empty;
    }

    private void OnCleanProgress(CleanProgress p)
    {
        if (p.Total > 0)
        {
            ProgressValue = Math.Clamp(p.Processed / (double)p.Total * 100, 0, 100);
        }

        var moved = p.RecycledBytes + p.FreedBytes;
        var verb = _moveToRecycleBin ? "Moving" : "Deleting";

        StatusText = $"{verb} {p.Processed:N0} / {p.Total:N0}\u2026  {ByteSizeFormatter.Format(moved)}";
        CurrentPathText = p.CurrentPath ?? string.Empty;
    }

    // ================================= reporting =============================

    private static string DescribeSkipped(ScanResult result)
    {
        if (result.Skipped.Count == 0)
        {
            return string.Empty;
        }

        var parts = result.Skipped
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Value:N0} {SkipReasonFormatter.Describe(kv.Key)}");

        return $"{result.SkippedCount:N0} entries skipped: {string.Join(", ", parts)}";
    }

    /// <summary>
    /// Keeps the outcome of a clean so it is still visible afterwards.
    /// </summary>
    /// <remarks>
    /// Persisted rather than only held in memory: the run most worth reviewing is an unattended
    /// one, which by definition happens while nobody is watching. The file is what lets the
    /// dashboard report a 3am clean in the morning.
    /// </remarks>
    /// <remarks>
    /// Only a finished clean is recorded. A cancelled one has no single outcome to report, and
    /// writing a partial result as "the last clean" would misrepresent it as one that completed.
    /// </remarks>
    private void RecordCleanReport(
        CleanResult result,
        int attemptedCount,
        RecycleBinStats binBefore,
        RecycleBinStats binAfter)
    {
        var report = CleanReport.From(
            result,
            attemptedCount,
            binBefore,
            binAfter,
            DateTimeOffset.UtcNow,
            CleanReportSources.App);

        _reports.Append(report);
        RefreshHistory();
    }

    /// <summary>
    /// Re-reads the recorded runs.
    /// </summary>
    /// <remarks>
    /// Public because a clean can also arrive from outside this window: the scheduled job runs the
    /// CLI in its own process, so the only way the dashboard learns about it is by asking the file
    /// again. The history is always read from disk for exactly that reason, and never cached.
    /// </remarks>
    public void RefreshHistory()
    {
        var history = _reports.LoadHistory();

        HistoryRows =
        [
            .. history.Take(MaxHistoryRows).Select(report => HistoryRow.From(report, DateTimeOffset.UtcNow)),
        ];

        HistorySummary = SummarizeHistory(history);
        ShowLastReport(history.FirstOrDefault());
    }

    /// <summary>Totals across the recorded runs, which is the part a single run cannot show.</summary>
    private static string SummarizeHistory(IReadOnlyList<CleanReport> history)
    {
        if (history.Count == 0)
        {
            return string.Empty;
        }

        var reclaimed = history.Sum(report => report.FreedBytes);
        var recycled = history.Sum(report => report.RecycledBytes);
        var oldest = history.Min(report => report.TimestampUtc);

        var parts = new List<string> { $"{history.Count:N0} runs recorded" };
        parts.Add($"{ByteSizeFormatter.Format(reclaimed)} reclaimed");

        if (recycled > 0)
        {
            parts.Add($"{ByteSizeFormatter.Format(recycled)} moved to the Recycle Bin");
        }

        parts.Add($"since {oldest.LocalDateTime:d MMM yyyy}");

        return string.Join(" \u00b7 ", parts);
    }

    /// <summary>Renders the last clean as a headline plus the before/after breakdown.</summary>
    private void ShowLastReport(CleanReport? report)
    {
        if (report is null)
        {
            LastCleanHeadline = null;
            LastCleanDetails = string.Empty;
            return;
        }

        var viaCli = report.Source.Equals("cli", StringComparison.OrdinalIgnoreCase)
            ? " \u00b7 command line"
            : string.Empty;

        var outcome = report.Mode == DeletionMode.RecycleBin
            ? $"Moved {ByteSizeFormatter.Format(report.RecycledBytes)} to the Recycle Bin"
            : $"Freed {ByteSizeFormatter.Format(report.FreedBytes)}";

        LastCleanHeadline = $"{outcome} \u00b7 {Humanize.Age(DateTimeOffset.UtcNow - report.TimestampUtc)}{viaCli}";

        var after = report.Mode == DeletionMode.RecycleBin
            ? $"After: {report.RecycledCount:N0} files moved, {report.FailedCount:N0} skipped. "
              + $"Of that, {ByteSizeFormatter.Format(report.ReclaimedBytes)} was reclaimed; the rest is "
              + "still occupying the disk inside the Recycle Bin."
            : $"After: {report.DeletedCount:N0} files deleted, {report.FailedCount:N0} skipped. "
              + $"Reclaimed {ByteSizeFormatter.Format(report.ReclaimedBytes)}.";

        LastCleanDetails =
            $"Before: {report.AttemptedCount:N0} files selected, {ByteSizeFormatter.Format(report.AttemptedBytes)}.\n"
            + after + "\n"
            + $"Recycle Bin: {ByteSizeFormatter.Format(report.RecycleBinBefore.SizeBytes)} before \u2192 "
            + $"{ByteSizeFormatter.Format(report.RecycleBinAfter.SizeBytes)} after.\n"
            + $"Took {report.Duration.TotalSeconds:0.0}s.";
    }

    private static string SummarizeFailures(CleanResult result)
    {
        var parts = result.Failures
            .GroupBy(f => f.Reason)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count():N0} {g.Key.ToLowerInvariant()}");

        return $"{result.FailedCount:N0} files could not be removed: {string.Join(", ", parts)}";
    }
}
