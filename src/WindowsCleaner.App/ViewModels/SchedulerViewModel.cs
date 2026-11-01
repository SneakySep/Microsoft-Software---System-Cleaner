using System.Globalization;
using System.IO;
using System.Windows.Input;
using WindowsCleaner.Core.Configuration;
using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Scheduling;
using WindowsCleaner.Core.Services;

namespace WindowsCleaner.App.ViewModels;

/// <summary>
/// Drives the scheduled cleaning dialog: what is registered right now, and changing it.
/// </summary>
/// <remarks>
/// <para>
/// The panel always reports what it read back from Task Scheduler rather than what it last asked
/// for. That distinction matters for a job that runs unattended: the definition can be changed
/// outside this app, and a panel showing its own remembered request would then describe a job
/// that no longer exists.
/// </para>
/// <para>
/// The freshness guard is fixed at <see cref="ScheduledCleanOptions.DefaultMinimumAge"/> rather
/// than exposed as a field. It is the main thing keeping an unattended job safe (nothing else
/// distinguishes it from a click by a user who can see what is about to be deleted), so it is
/// not something the UI should invite anyone to weaken.
/// </para>
/// </remarks>
public sealed class SchedulerViewModel : ObservableObject
{
    private readonly ISchedulerService _scheduler;
    private readonly SettingsStore _settings;
    private readonly CleanReportStore _reports;
    private readonly Func<string?> _pickExecutable;
    private readonly Func<string, bool> _confirm;

    private bool _isInstalled;
    private bool _isStateKnown = true;
    private string _statusHeadline = "Checking\u2026";
    private string _statusDetail = string.Empty;
    private string? _guardNote;
    private ScheduleFrequency _frequency = ScheduleFrequency.Daily;
    private DayOfWeek _selectedDay = DayOfWeek.Sunday;
    private string _timeText = "03:00";
    private DeletionMode _mode = DeletionMode.Permanent;
    private string _messageText = string.Empty;
    private bool _messageIsError;
    private string? _cliPath;
    private string _cliSummary = string.Empty;
    private bool _showBrowseForCli;
    private string _lastRunText = string.Empty;
    private string _nextRunText = string.Empty;
    private bool _isRunning;
    private CancellationTokenSource? _cancellation;

    public SchedulerViewModel(
        ISchedulerService scheduler,
        SettingsStore settings,
        CleanReportStore reports,
        Func<string?>? pickExecutable = null,
        Func<string, bool>? confirm = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _pickExecutable = pickExecutable ?? (() => null);

        // Refusing by default: a confirmation that cannot be shown must not become consent for
        // running a destructive command.
        _confirm = confirm ?? (_ => false);

        Days = [.. Enum.GetValues<DayOfWeek>().Select(day => new ScheduleDayOption(this, day))];

        ApplyCommand = new RelayCommand(Apply);
        DisableCommand = new RelayCommand(Disable, () => IsInstalled);
        BrowseCommand = new RelayCommand(Browse, () => ShowBrowseForCli);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        RunNowCommand = new AsyncRelayCommand(RunNowAsync, () => IsInstalled && !IsRunning);
        StopCommand = new RelayCommand(Stop, () => IsRunning);

        Refresh();
    }

    /// <summary>Raised when the user asks to close the dialog.</summary>
    public event EventHandler? CloseRequested;

    public ICommand ApplyCommand { get; }
    public ICommand DisableCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand RunNowCommand { get; }
    public ICommand StopCommand { get; }

    /// <summary>Selectable weekdays, shown only for a weekly schedule.</summary>
    public IReadOnlyList<ScheduleDayOption> Days { get; }

    public bool IsInstalled
    {
        get => _isInstalled;
        private set
        {
            if (SetProperty(ref _isInstalled, value))
            {
                OnPropertyChanged(nameof(IsNotInstalled));
                OnPropertyChanged(nameof(CanRunNow));
                RaiseCommandStates();
            }
        }
    }

    public bool IsNotInstalled => !_isInstalled;

    /// <summary>False when the scheduler could not be consulted at all.</summary>
    public bool IsStateKnown
    {
        get => _isStateKnown;
        private set => SetProperty(ref _isStateKnown, value);
    }

    public string StatusHeadline
    {
        get => _statusHeadline;
        private set => SetProperty(ref _statusHeadline, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    /// <summary>Explains a difference between the registered job and what applying would write.</summary>
    public string? GuardNote
    {
        get => _guardNote;
        private set
        {
            if (SetProperty(ref _guardNote, value))
            {
                OnPropertyChanged(nameof(HasGuardNote));
            }
        }
    }

    public bool HasGuardNote => !string.IsNullOrEmpty(_guardNote);

    public bool IsDaily
    {
        get => _frequency == ScheduleFrequency.Daily;
        set => SelectFrequency(ScheduleFrequency.Daily, value);
    }

    public bool IsWeekly
    {
        get => _frequency == ScheduleFrequency.Weekly;
        set => SelectFrequency(ScheduleFrequency.Weekly, value);
    }

    /// <summary>Shows the weekday picker, which only means anything for a weekly schedule.</summary>
    public bool IsWeeklySchedule => _frequency == ScheduleFrequency.Weekly;

    public string TimeText
    {
        get => _timeText;
        set => SetProperty(ref _timeText, value);
    }

    public bool IsPermanentMode
    {
        get => _mode == DeletionMode.Permanent;
        set => SelectMode(DeletionMode.Permanent, value);
    }

    public bool IsRecycleMode
    {
        get => _mode == DeletionMode.RecycleBin;
        set => SelectMode(DeletionMode.RecycleBin, value);
    }

    /// <summary>When the scheduled job last ran, from the record it left behind.</summary>
    public string LastRunText
    {
        get => _lastRunText;
        private set => SetProperty(ref _lastRunText, value);
    }

    /// <summary>When it is next due, computed from the definition shown above it.</summary>
    public string NextRunText
    {
        get => _nextRunText;
        private set => SetProperty(ref _nextRunText, value);
    }

    /// <summary>True while a run started from here is in progress.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanRunNow));
                OnPropertyChanged(nameof(ShowRunNow));
                OnPropertyChanged(nameof(ShowStop));
                RaiseCommandStates();
            }
        }
    }

    public bool CanRunNow => IsInstalled && !IsRunning;

    /// <summary>
    /// Which of the two run controls is showing.
    /// </summary>
    /// <remarks>
    /// A pair of plain flags rather than an inverting converter, which would be a new class and a
    /// resource entry for one boolean.
    /// </remarks>
    public bool ShowRunNow => !IsRunning;

    public bool ShowStop => IsRunning;

    public string MessageText
    {
        get => _messageText;
        private set
        {
            if (SetProperty(ref _messageText, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => _messageText.Length > 0;

    public bool MessageIsError
    {
        get => _messageIsError;
        private set => SetProperty(ref _messageIsError, value);
    }

    /// <summary>Which executable the job will run, once one has been found.</summary>
    public string? CliPath
    {
        get => _cliPath;
        private set => SetProperty(ref _cliPath, value);
    }

    public string CliSummary
    {
        get => _cliSummary;
        private set => SetProperty(ref _cliSummary, value);
    }

    /// <summary>
    /// True when the command-line tool could not be found, so the user is offered the file picker.
    /// </summary>
    public bool ShowBrowseForCli
    {
        get => _showBrowseForCli;
        private set
        {
            if (SetProperty(ref _showBrowseForCli, value))
            {
                (BrowseCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Reads the current state and the executable to schedule.</summary>
    public void Refresh()
    {
        ResolveCli();

        var state = _scheduler.GetState();

        if (state.Error is not null)
        {
            IsStateKnown = false;
            IsInstalled = false;
            StatusHeadline = "Scheduled cleaning: state unknown";
            StatusDetail = state.Error;
            GuardNote = null;
            RefreshRunInformation(null);
            return;
        }

        IsStateKnown = true;

        if (!state.IsInstalled || state.Snapshot is not { } snapshot)
        {
            IsInstalled = false;
            StatusHeadline = "Scheduled cleaning is off";
            StatusDetail = "Nothing runs automatically. Turn it on to clean unattended on a schedule.";
            GuardNote = null;
            RefreshRunInformation(null);
            return;
        }

        IsInstalled = true;
        StatusHeadline = "Scheduled cleaning is on";

        var guard = snapshot.MinimumAge is { } age
            ? $"\nOnly files untouched for {Humanize.Duration(age)} are cleaned."
            : string.Empty;

        StatusDetail = $"{snapshot.DescribeSchedule()} \u00b7 {DescribeMode(snapshot.Mode)}{guard}"
            + $"\n{snapshot.DescribeCommand()}";

        GuardNote = snapshot.MinimumAge is { } registered && registered != ScheduledCleanOptions.DefaultMinimumAge
            ? $"The registered job uses a {Humanize.Duration(registered)} guard; applying these settings will set it to "
              + $"{Humanize.Duration(ScheduledCleanOptions.DefaultMinimumAge)}."
            : null;

        // Prefilled from what is actually registered, so updating means changing something rather
        // than silently resetting the job to this dialog's defaults.
        _frequency = snapshot.Frequency;
        _selectedDay = snapshot.Day ?? DayOfWeek.Sunday;
        _timeText = ScheduleText.FormatTimeOfDay(snapshot.TimeOfDay);
        _mode = snapshot.Mode;

        RefreshRunInformation(snapshot);
        RaiseScheduleChanged();
    }

    /// <summary>
    /// Works out when the job last ran and when it is next due.
    /// </summary>
    /// <remarks>
    /// "Last ran" comes from the record the CLI writes, not from the scheduler: the only way to ask
    /// Task Scheduler is its own report, whose labels are localised. Runs are told apart by the
    /// origin the job stamps on them, which is why that is set when the job is registered.
    /// </remarks>
    private void RefreshRunInformation(ScheduledTaskSnapshot? snapshot)
    {
        var last = _reports.LoadHistory().FirstOrDefault(report =>
            report.Source.Equals(CleanReportSources.Scheduled, StringComparison.OrdinalIgnoreCase));

        LastRunText = last is null
            ? "It has not run yet."
            : $"It last ran {Humanize.Age(DateTimeOffset.UtcNow - last.TimestampUtc)}: "
              + Describe(last);

        if (snapshot is null)
        {
            NextRunText = string.Empty;
            return;
        }

        NextRunText = snapshot.GetNextRun(DateTimeOffset.Now) is { } next
            ? $"Next run {DescribeNext(next)}."
            : $"Repeats {snapshot.DescribeSchedule()}; the next date is left to Task Scheduler, which "
              + "counts repeats from when the job was first registered.";
    }

    /// <summary>
    /// Runs the registered job now, after asking.
    /// </summary>
    /// <remarks>
    /// What runs is what the scheduler holds, not what the form currently shows: "run the job now"
    /// should mean the job. It also means the button is safe to press with unsaved changes in the
    /// form, which is not otherwise the case for a destructive action.
    /// </remarks>
    private async Task RunNowAsync()
    {
        var state = _scheduler.GetState();

        if (!state.IsInstalled || state.Snapshot is not { } snapshot)
        {
            SetMessage("There is no scheduled job to run.", isError: true);
            return;
        }

        var guard = snapshot.MinimumAge is { } age
            ? $"anything modified in the last {Humanize.Duration(age)}"
            : "anything modified recently";

        // No claim is made about which locations it touches: a job can have been registered with
        // its own --target, and the mode and guard below are what this app can vouch for.
        var question =
            "Run the scheduled job now?\n\n"
            + $"It will {DescribeAction(snapshot.Mode)}, skipping {guard}.\n\n"
            + "This is the same thing the job does on its own schedule.";

        if (!_confirm(question))
        {
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;

        IsRunning = true;
        SetMessage("Running the scheduled job\u2026", isError: false);

        _cancellation = new CancellationTokenSource();

        try
        {
            var result = await _scheduler.RunNowAsync(_cancellation.Token);

            SetMessage(DescribeRun(result, startedAt), isError: !result.Succeeded && !result.Cancelled);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetMessage($"The run failed: {e.Message}", isError: true);
        }
        finally
        {
            IsRunning = false;
            _cancellation?.Dispose();
            _cancellation = null;

            // The run recorded itself, so the last-run line is re-read rather than assumed.
            Refresh();
        }
    }

    private void Stop()
    {
        // Cooperative: the cleaner checks between files and unwinds, so what it has already
        // removed stays removed and the rest of the list is left alone.
        _cancellation?.Cancel();
    }

    /// <summary>
    /// Describes a finished run from the record it wrote, falling back to the command's own output
    /// for the cases where it records nothing.
    /// </summary>
    private string DescribeRun(ScheduledRunResult result, DateTimeOffset startedAt)
    {
        if (result.StartError is not null)
        {
            return $"Could not start the job: {result.StartError}";
        }

        if (result.Cancelled)
        {
            return "Stopped. Files it had already removed stay removed.";
        }

        var report = _reports.LoadLast();

        // The timestamp is what makes this the record of *this* run rather than a leftover one: a
        // run that finds nothing to clean exits without recording anything at all.
        if (report is not null && report.TimestampUtc >= startedAt.AddSeconds(-2))
        {
            return $"Finished: {Describe(report)}.";
        }

        var detail = FirstMeaningfulLine(result.Output);

        return detail.Length > 0
            ? $"Finished (exit code {result.ExitCode}): {detail}"
            : $"Finished with exit code {result.ExitCode}.";
    }

    /// <summary>Picks the line of the CLI's report worth repeating, if there is one.</summary>
    private static string FirstMeaningfulLine(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Space freed", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Moved to Recycle Bin", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Nothing to clean", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }

        return string.Empty;
    }

    private static string Describe(CleanReport report) => report.Mode == DeletionMode.RecycleBin
        ? $"moved {Core.Utilities.ByteSizeFormatter.Format(report.RecycledBytes)} "
          + $"({report.RecycledCount:N0} files) to the Recycle Bin"
        : $"freed {Core.Utilities.ByteSizeFormatter.Format(report.FreedBytes)} ({report.DeletedCount:N0} files)";

    private static string DescribeAction(DeletionMode mode) => mode == DeletionMode.RecycleBin
        ? "move files to the Recycle Bin"
        : "delete files permanently";

    private static string DescribeNext(DateTimeOffset next)
    {
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var date = DateOnly.FromDateTime(next.LocalDateTime);
        var time = next.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);

        return date == today
            ? $"today at {time}"
            : date == today.AddDays(1)
                ? $"tomorrow at {time}"
                : $"on {next.LocalDateTime:ddd d MMM} at {time}";
    }

    private void RaiseCommandStates()
    {
        (DisableCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (BrowseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RunNowCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>Called by <see cref="ScheduleDayOption"/>; the day itself is owned here.</summary>
    internal void SelectDay(DayOfWeek day)
    {
        _selectedDay = day;
        RaiseDaySelectionChanged();
    }

    internal void RaiseDaySelectionChanged()
    {
        foreach (var option in Days)
        {
            option.RaiseSelectionChanged();
        }
    }

    internal bool IsDaySelected(DayOfWeek day) => _selectedDay == day;

    private void SelectFrequency(ScheduleFrequency frequency, bool selected)
    {
        if (selected)
        {
            _frequency = frequency;
            RaiseScheduleChanged();
        }
        else if (_frequency == frequency)
        {
            // A toggle can be clicked off, but this is a one-of-two choice; re-notify so the
            // button snaps back instead of drifting out of sync with the value it shows.
            RaiseScheduleChanged();
        }
    }

    private void SelectMode(DeletionMode mode, bool selected)
    {
        if (selected)
        {
            _mode = mode;
            RaiseModeChanged();
        }
        else if (_mode == mode)
        {
            RaiseModeChanged();
        }
    }

    private void RaiseScheduleChanged()
    {
        OnPropertyChanged(nameof(IsDaily));
        OnPropertyChanged(nameof(IsWeekly));
        OnPropertyChanged(nameof(IsWeeklySchedule));
        RaiseDaySelectionChanged();
    }

    private void RaiseModeChanged()
    {
        OnPropertyChanged(nameof(IsPermanentMode));
        OnPropertyChanged(nameof(IsRecycleMode));
    }

    private void Apply()
    {
        if (!TryReadTimeOfDay(TimeText, out var timeOfDay))
        {
            SetMessage($"'{TimeText}' is not a time of day. Use 24-hour HH:mm, for example 03:30.", isError: true);
            return;
        }

        var options = new ScheduledCleanOptions
        {
            Frequency = _frequency,
            Day = _selectedDay,
            TimeOfDay = timeOfDay,
            Mode = _mode,
            MinimumAge = ScheduledCleanOptions.DefaultMinimumAge,
            ExecutablePath = CliPath,
        };

        var result = _scheduler.Enable(options);

        SetMessage(result.Message, !result.Succeeded);

        // Read the result back rather than trusting the attempted change.
        Refresh();
    }

    private void Disable()
    {
        var result = _scheduler.Disable();
        SetMessage(result.Message, !result.Succeeded);
        Refresh();
    }

    private void Browse()
    {
        var picked = _pickExecutable();
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        if (!File.Exists(picked))
        {
            SetMessage($"That file does not exist: {picked}", isError: true);
            return;
        }

        // Remembered so the user is not asked for the same file every time they open this dialog.
        SettingsEditor.Update(_settings, settings => settings.CliExecutablePath = picked);

        ResolveCli();
        SetMessage($"The job will run {picked}.", isError: false);
    }

    /// <summary>
    /// Works out which executable the job should run: the remembered choice, then the automatic
    /// search.
    /// </summary>
    private void ResolveCli()
    {
        var remembered = _settings.Load().CliExecutablePath;

        if (!string.IsNullOrWhiteSpace(remembered) && File.Exists(remembered))
        {
            CliPath = remembered;
            CliSummary = $"The job runs {remembered}";
            ShowBrowseForCli = false;
            return;
        }

        if (_scheduler.TryLocateCli(out var located, out _) && located is not null)
        {
            CliPath = located;
            CliSummary = $"The job runs {located}";
            ShowBrowseForCli = false;
            return;
        }

        CliPath = null;
        CliSummary = $"Could not find {WcleanLocator.ExecutableName}, which the job has to run.";
        ShowBrowseForCli = true;
    }

    private void SetMessage(string message, bool isError)
    {
        MessageText = message;
        MessageIsError = isError;
    }

    /// <summary>Accepts 24-hour <c>HH:mm</c>, tolerating a single-digit hour.</summary>
    private static bool TryReadTimeOfDay(string text, out TimeSpan timeOfDay)
    {
        timeOfDay = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        if (!TimeOnly.TryParseExact(trimmed, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            && !TimeOnly.TryParseExact(trimmed, "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
        {
            return false;
        }

        timeOfDay = time.ToTimeSpan();
        return true;
    }

    private static string DescribeMode(DeletionMode mode) => mode == DeletionMode.RecycleBin
        ? "moves files to the Recycle Bin"
        : "deletes files permanently";

}

/// <summary>One selectable weekday in the schedule dialog.</summary>
public sealed class ScheduleDayOption(SchedulerViewModel owner, DayOfWeek day) : ObservableObject
{
    public DayOfWeek Day { get; } = day;

    public string Label { get; } = day.ToString()[..3].ToUpperInvariant();

    /// <summary>
    /// Backed by the view model rather than by a field here, so the group can never end up with
    /// two days ticked or none at all.
    /// </summary>
    public bool IsSelected
    {
        get => owner.IsDaySelected(Day);
        set
        {
            if (value)
            {
                owner.SelectDay(Day);
            }
            else
            {
                // Unticking the current day is not a state this control has; snap it back.
                owner.RaiseDaySelectionChanged();
            }
        }
    }

    internal void RaiseSelectionChanged() => OnPropertyChanged(nameof(IsSelected));
}
