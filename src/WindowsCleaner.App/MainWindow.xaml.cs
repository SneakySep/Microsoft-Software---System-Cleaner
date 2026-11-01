using System.Windows;

namespace WindowsCleaner.App;

/// <summary>
/// The dashboard window.
/// </summary>
/// <remarks>
/// Intentionally empty: all behaviour lives in <c>MainViewModel</c>, which is assigned to
/// <see cref="FrameworkElement.DataContext"/> by the composition root in <c>App</c>. Keeping
/// the code-behind free of logic is what lets the whole view model be unit tested.
/// </remarks>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
