using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Veyra.Desktop.Native;

public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, bool>? _can;
    private readonly Func<object?, Task>? _execAsync;
    private readonly Action<object?>? _exec;

    public RelayCommand(Action<object?> exec, Predicate<object?>? can = null)
    { _exec = exec; if (can != null) _can = p => can(p); }

    public RelayCommand(Func<object?, Task> execAsync, Predicate<object?>? can = null)
    { _execAsync = execAsync; if (can != null) _can = p => can(p); }

    public bool CanExecute(object? p) => _can?.Invoke(p) ?? true;
    public async void Execute(object? p) { if (_execAsync != null) await _execAsync(p); else _exec?.Invoke(p); }

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
