using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Setup;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Native;

namespace Veyra.Desktop.ViewModels.Pages.SetupWizard;

public sealed class SetupWizardViewModel : INotifyPropertyChanged
{
    private readonly IMediator _mediator;
    private readonly ILogger<SetupWizardViewModel> _log;
    private readonly SelectDirectoriesViewModel _dirsVm;
    private readonly SelectFormatsViewModel _formatsVm;
    private readonly RepositoryNameViewModel _repoNameVm;
    private readonly object[] _steps;
    private int _index;

    public event System.EventHandler? RequestClose;
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    public RelayCommand BackCommand { get; }
    public RelayCommand NextCommand { get; }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); }
    }

    public SetupWizardViewModel(
        IMediator mediator,
        ILogger<SetupWizardViewModel> log,
        SelectDirectoriesViewModel dirsVm,
        SelectFormatsViewModel formatsVm)
    {
        _mediator = mediator;
        _log = log;
        _dirsVm = dirsVm;
        _formatsVm = formatsVm;
        _repoNameVm = new RepositoryNameViewModel();

        _steps = new object[] { _dirsVm, _formatsVm, _repoNameVm };
        _index = 0;

        BackCommand = new RelayCommand(_ => Back(), _ => _index > 0);
        NextCommand = new RelayCommand(async _ => await NextAsync(), _ => CanGoNext);

        _dirsVm.SelectionChanged += (_, _) => RefreshCommands();
        _formatsVm.SelectionChanged += (_, _) => RefreshCommands();
        _repoNameVm.SelectionChanged += (_, _) => RefreshCommands();

        LocalizationManager.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(NextButtonText));
            OnPropertyChanged(nameof(StepInfo));
        };
    }

    public object CurrentStep => _steps[_index];

    public bool CanGoNext =>
        (CurrentStep is SelectDirectoriesViewModel d && d.HasAny) ||
        (CurrentStep is SelectFormatsViewModel f && f.HasAny) ||
        (CurrentStep is RepositoryNameViewModel r && r.HasAny);

    public bool IsLastStep => _index == _steps.Length - 1;
    public string NextButtonText => IsLastStep ? Loc.T("setup_wizard.finish") : Loc.T("setup_wizard.next");

    public string StepInfo => Loc.F("setup_wizard.step_info", _index + 1, _steps.Length);

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanGoNext));
        NextCommand.RaiseCanExecuteChanged();
    }

    private void Back()
    {
        if (_index == 0) return;
        _index--;
        ErrorMessage = null;
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextButtonText));
        OnPropertyChanged(nameof(StepInfo));
        BackCommand.RaiseCanExecuteChanged();
        RefreshCommands();
    }

    private async Task NextAsync()
    {
        ErrorMessage = null;

        if (CurrentStep is SelectDirectoriesViewModel)
        {
            if (!await _dirsVm.CommitAsync()) return;
            GoForward();
            return;
        }

        if (CurrentStep is SelectFormatsViewModel)
        {
            if (!await _formatsVm.CommitAsync()) return;
            GoForward();
            return;
        }

        if (CurrentStep is RepositoryNameViewModel)
            await FinalSaveAsync();
    }

    private void GoForward()
    {
        _index++;
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextButtonText));
        OnPropertyChanged(nameof(StepInfo));
        BackCommand.RaiseCanExecuteChanged();
        RefreshCommands();
    }

    private async Task FinalSaveAsync()
    {
        try
        {
            var dirs = _dirsVm.GetSelectedDirectories();
            var exts = _formatsVm.GetSelectedExtensions();
            var repoName = _repoNameVm.RepositoryName.Trim();

            var result = await _mediator.Send(new SaveInitialSetupCommand(dirs, exts, repoName));

            if (!result.Success)
            {
                ErrorMessage = result.Error ?? Loc.T("setup_wizard.save_failed");
                _log.LogError("Initial setup failed: {Error}", result.Error);
                return;
            }

            _log.LogInformation("Initial setup completed: {Dirs} dirs, {Exts} extensions, repo: {Name}",
                dirs.Count, exts.Count, repoName);

            RequestClose?.Invoke(this, System.EventArgs.Empty);
        }
        catch (System.Exception ex)
        {
            _log.LogError(ex, "Failed to save initial setup");
            ErrorMessage = Loc.F("setup_wizard.error_with_message", ex.Message);
        }
    }
}
