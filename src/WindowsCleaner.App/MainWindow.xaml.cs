using System.Windows;
using WindowsCleaner.App.Theme;

namespace WindowsCleaner.App;

/// <summary>
/// The dashboard window.
/// </summary>
/// <remarks>
/// Intentionally almost empty: all behaviour lives in <c>MainViewModel</c>, which is assigned to
/// <see cref="FrameworkElement.DataContext"/> by the composition root in <c>App</c>. Keeping
/// the code-behind free of logic is what lets the whole view model be unit tested. The one
/// exception is the system backdrop, which is presentation, not behaviour: it needs the Win32
/// handle this window owns and nothing else in the application does.
/// </remarks>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) => Backdrop.Apply(this);
    }
}
