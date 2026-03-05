using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MediatR;
using Veyra.Desktop.Native;

namespace Veyra.Desktop.ViewModels.Pages.SetupWizard;

public sealed class SetupWizardViewModel : INotifyPropertyChanged
{
    private readonly SelectDirectoriesViewModel _dirsVm;
    private readonly SelectFormatsViewModel _formatsVm;
    private readonly object[] _steps;
    private int _index;

    public event System.EventHandler? RequestClose;
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    public RelayCommand BackCommand { get; }
    public RelayCommand NextCommand { get; }

    public SetupWizardViewModel(
        IMediator mediator,
        SelectDirectoriesViewModel dirsVm,
        SelectFormatsViewModel formatsVm)
    {
        _dirsVm = dirsVm;
        _formatsVm = formatsVm;

        // Steps are ViewModels — View will resolve them via DataTemplates
        _steps = new object[] { _dirsVm, _formatsVm };
        _index = 0;

        BackCommand = new RelayCommand(_ => Back(), _ => _index > 0);
        NextCommand = new RelayCommand(async _ => await NextAsync(), _ => CanGoNext);

        _dirsVm.SelectionChanged += (_, _) => RefreshCommands();
        _formatsVm.SelectionChanged += (_, _) => RefreshCommands();
    }

    /// <summary>Current step ViewModel (bound via ContentControl + DataTemplates)</summary>
    public object CurrentStep => _steps[_index];

    public bool CanGoNext =>
        (CurrentStep is SelectDirectoriesViewModel d && d.HasAny) ||
        (CurrentStep is SelectFormatsViewModel f && f.HasAny);

    public bool IsLastStep => _index == _steps.Length - 1;
    public string NextButtonText => IsLastStep ? "Завершить" : "Далее";

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanGoNext));
        NextCommand.RaiseCanExecuteChanged();
    }

    private void Back()
    {
        if (_index == 0) return;
        _index--;
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextButtonText));
        BackCommand.RaiseCanExecuteChanged();
        RefreshCommands();
    }

    private async Task NextAsync()
    {
        if (CurrentStep is SelectDirectoriesViewModel)
        {
            if (!await _dirsVm.CommitAsync()) return;
            _index++;
            OnPropertyChanged(nameof(CurrentStep));
            OnPropertyChanged(nameof(IsLastStep));
            OnPropertyChanged(nameof(NextButtonText));
            BackCommand.RaiseCanExecuteChanged();
            RefreshCommands();
            return;
        }

        if (CurrentStep is SelectFormatsViewModel)
        {
            if (!await _formatsVm.CommitAsync()) return;
            RequestClose?.Invoke(this, System.EventArgs.Empty);
        }
    }
}
