using System;
using System.Windows.Input;
using Avalonia.Labs.Input;

namespace Veyra.Desktop.Native;

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _exec;
    private readonly Predicate<object?>? _can;

    public RelayCommand(Action<object?> exec, Predicate<object?>? can = null)
    { _exec = exec; _can = can; }

    public bool CanExecute(object? p) => _can?.Invoke(p) ?? true;
    public void Execute(object? p) => _exec(p);
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value!;
        remove => CommandManager.RequerySuggested -= value!;
    }
}
