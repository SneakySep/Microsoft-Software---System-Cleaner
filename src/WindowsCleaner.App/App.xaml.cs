using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WindowsCleaner.App.ViewModels;
using WindowsCleaner.Core.Configuration;
using WindowsCleaner.Core.Models;
using WindowsCleaner.Core.Services;
using WindowsCleaner.Core.Targets;
using WindowsCleaner.Core.Utilities;

namespace WindowsCleaner.App;

/// <summary>
/// Application entry point and composition root.
/// </summary>
/// <remarks>
/// The object graph is wired by hand. It is a handful of objects deep, so a DI container would
/// add a dependency and a layer of indirection without earning either. If the app grows much
/// past this, it is the place to introduce one.
/// </remarks>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Last line of defence. Individual scan/clean operations already handle their own
        // failures; this exists so an unforeseen UI-thread exception shows a message instead
        // of silently killing the process with no explanation.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var settingsStore = new SettingsStore();
        var settings = settingsStore.Load();
        var reports = new CleanReportStore();

        // The provider is built once and shared by reference, so the view model can change the
        // user's folder list without the scanner and cleaner needing to be rebuilt.
        var targetProvider = new CompositeTargetProvider(
            new DefaultCleanTargetProvider(),
            settings.ExtraFolders);

        var scheduler = new SchedulerService();

        var viewModel = new MainViewModel(
            new TempScanner(targetProvider),
            new CleanerService(targetProvider),
            new RecycleBinService(),
            settingsStore,
            targetProvider,
            reports,
            new MainWindowActions(ConfirmEmptyRecycleBin, RequestElevation));

        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        MainWindow = window;

        // The dialog is built on demand rather than up front, so every opening re-reads what the
        // scheduler actually holds instead of showing state from app startup.
        viewModel.SchedulerRequested += (_, _) =>
        {
            var dialog = new SchedulerWindow(new SchedulerViewModel(
                scheduler,
                settingsStore,
                reports,
                PickWcleanExecutable,
                ConfirmRunScheduledJob))
            {
                Owner = window,
            };

            dialog.ShowDialog();

            // The job may have run, or been registered or removed, while the dialog was open. The
            // dashboard re-reads rather than carrying a stale picture of either.
            viewModel.RefreshHistory();
        };

        window.Show();
    }

    /// <summary>
    /// Asks before destroying the Recycle Bin's contents, naming exactly what goes.
    /// </summary>
    private static bool ConfirmEmptyRecycleBin(RecycleBinStats stats) =>
        MessageBox.Show(
            $"Permanently delete everything in the Recycle Bin?\n\n"
            + $"{stats.ItemCount:N0} items, {ByteSizeFormatter.Format(stats.SizeBytes)}.\n\n"
            + "Files emptied from the Recycle Bin cannot be recovered.",
            "Empty the Recycle Bin",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>
    /// Asks before running the scheduled job, which deletes files without showing them first.
    /// </summary>
    private static bool ConfirmRunScheduledJob(string question) =>
        MessageBox.Show(
            question,
            "Run the scheduled job",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>
    /// Restarts this program elevated, and closes this copy once the new one is up.
    /// </summary>
    /// <remarks>
    /// The shutdown lives here rather than in the view model: deciding when the process ends is
    /// the application's business. It happens only after the elevated copy has actually been
    /// started, so declining the prompt leaves the running app completely untouched.
    /// </remarks>
    private static ElevationResult RequestElevation()
    {
        var executable = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(executable))
        {
            return new ElevationResult(false, "Could not work out which program to restart.");
        }

        var result = ElevationLauncher.TryRelaunchElevated(executable);

        if (result.Started)
        {
            Current.Shutdown();
        }

        return result;
    }

    /// <summary>
    /// Lets the user point the scheduled job at a <c>wclean.exe</c> the automatic search missed.
    /// </summary>
    private static string? PickWcleanExecutable()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select wclean.exe",
            Filter = "Windows Cleaner command line (wclean.exe)|wclean.exe|Executables (*.exe)|*.exe",
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}",
            "Windows Cleaner",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
