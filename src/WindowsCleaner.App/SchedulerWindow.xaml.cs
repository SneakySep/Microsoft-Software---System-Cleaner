using System.Windows;
using WindowsCleaner.App.Theme;
using WindowsCleaner.App.ViewModels;

namespace WindowsCleaner.App;

/// <summary>
/// The scheduled cleaning dialog.
/// </summary>
/// <remarks>
/// The only things here are the close wiring and the system backdrop: the view model has no
/// business knowing about <see cref="Window"/>, and the dialog stays disposable rather than being
/// kept alive as a hidden singleton, so every opening reflects the scheduler's current state.
/// </remarks>
public partial class SchedulerWindow : Window
{
    public SchedulerWindow(SchedulerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();

        SourceInitialized += (_, _) => Backdrop.Apply(this);
    }
}
