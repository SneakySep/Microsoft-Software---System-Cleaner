using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WindowsCleaner.App.ViewModels;

/// <summary>
/// Minimal <see cref="INotifyPropertyChanged"/> implementation.
/// </summary>
/// <remarks>
/// Hand-rolled rather than pulled from a toolkit package: the app needs exactly this much
/// MVVM plumbing and nothing more, and a single small base class is cheaper than a dependency.
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Sets a field and raises a change notification only when the value actually differs.</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
