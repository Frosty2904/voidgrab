using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace VoidGrab.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>
/// A command whose enabled state is raised explicitly.
/// </summary>
/// <remarks>
/// WPF's <c>CommandManager.RequerySuggested</c> would be less code, but it does
/// not exist outside WPF and this type is now shared with the Avalonia head.
/// Raising <see cref="CanExecuteChanged"/> by hand is also the more honest
/// mechanism: requery-on-every-UI-event re-evaluates every command constantly
/// and obscures exactly when a button's state is meant to change.
///
/// <see cref="ICommand"/> itself is portable — it lives in System.ObjectModel,
/// not in any UI assembly.
/// </remarks>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
