using System.Windows;
using WindowsCleaner.App.ViewModels;

namespace WindowsCleaner.App;

/// <summary>
/// The scheduled cleaning dialog.
/// </summary>
/// <remarks>
/// The only thing here is the close wiring: the view model has no business knowing about
/// <see cref="Window"/>, and the dialog stays disposable rather than being kept alive as a hidden
/// singleton, so every opening reflects the scheduler's current state.
/// </remarks>
public partial class SchedulerWindow : Window
{
    public SchedulerWindow(SchedulerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }
}
