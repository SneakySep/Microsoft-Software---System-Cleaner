using System.Windows.Input;

namespace WindowsCleaner.App.ViewModels;

/// <summary>A synchronous <see cref="ICommand"/> backed by delegates.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A parameterised <see cref="ICommand"/> backed by delegates.</summary>
public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(Coerce(parameter)) ?? true;

    public void Execute(object? parameter) => execute(Coerce(parameter));

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static T Coerce(object? parameter) => parameter is T typed ? typed : default!;
}

/// <summary>
/// An <see cref="ICommand"/> for <c>async</c> work.
/// </summary>
/// <remarks>
/// The reentrancy guard matters more than it looks: without it, a user can click "Scan"
/// repeatedly during a long scan and stack up overlapping scans. <see cref="CanExecute"/>
/// reports false while a run is in flight, which disables the bound button.
/// </remarks>
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();

        try
        {
            await execute();
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
