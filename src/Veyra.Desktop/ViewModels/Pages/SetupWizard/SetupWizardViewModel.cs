using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Setup;
using Veyra.Application.DTOs;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Native;

namespace Veyra.Desktop.ViewModels.Pages.SetupWizard;

public sealed record SetupWizardProgressLogItemViewModel(string TimestampText, string Message);

public sealed class SetupWizardViewModel : INotifyPropertyChanged
{
    private readonly IMediator _mediator;
    private readonly ILogger<SetupWizardViewModel> _log;
    private readonly SelectDirectoriesViewModel _dirsVm;
    private readonly SelectFormatsViewModel _formatsVm;
    private readonly RepositoryNameViewModel _repoNameVm;
    private readonly object[] _steps;

    private int _index;
    private bool _isBusy;
    private bool _isCompleted;
    private string? _errorMessage;
    private int _progressPercent;
    private string _progressMessage = Loc.T("create_repo.waiting_start");
    private int _filesProcessed;
    private int _filesTotal;
    private bool _isProgressIndeterminate;
    private string? _lastProgressLogSignature;

    public event EventHandler? RequestClose;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SetupWizardProgressLogItemViewModel> ProgressLogItems { get; } = [];

    public RelayCommand BackCommand { get; }
    public RelayCommand NextCommand { get; }

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

        _steps = [_dirsVm, _formatsVm, _repoNameVm];
        _index = 0;

        BackCommand = new RelayCommand(_ => Back(), _ => CanGoBack);
        NextCommand = new RelayCommand(async _ => await NextAsync(), _ => CanGoNext);

        _dirsVm.SelectionChanged += (_, _) => RefreshStepState();
        _formatsVm.SelectionChanged += (_, _) => RefreshStepState();
        _repoNameVm.SelectionChanged += (_, _) => RefreshStepState();

        LocalizationManager.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(NextButtonText));
            OnPropertyChanged(nameof(StepInfo));
            OnPropertyChanged(nameof(TrackedFolderCount));
            OnPropertyChanged(nameof(SelectedFormatCount));
        };
    }

    public object CurrentStep => _steps[_index];

    public bool IsLastStep => _index == _steps.Length - 1;

    public bool CanGoBack => _index > 0 && !IsBusy && !IsCompleted;

    public bool CanGoNext
    {
        get
        {
            if (IsBusy)
                return false;

            if (IsLastStep)
                return IsCompleted || _repoNameVm.HasAny;

            return (CurrentStep is SelectDirectoriesViewModel d && d.HasAny) ||
                   (CurrentStep is SelectFormatsViewModel f && f.HasAny);
        }
    }

    public bool ShowScanSection => IsLastStep && (IsBusy || IsCompleted || HasProgressLog);
    public bool ShowStepContent => !ShowScanSection;
    public bool HasProgressLog => ProgressLogItems.Count > 0;
    public int FilesFoundCount => Math.Max(FilesProcessed, FilesTotal);
    public int SelectedFormatCount => _formatsVm.GetSelectedExtensions().Count;
    public int TrackedFolderCount => _dirsVm.GetSelectedDirectories().Count;

    public string NextButtonText => !IsLastStep
        ? Loc.T("setup_wizard.next")
        : IsCompleted
            ? Loc.T("create_repo.done")
            : IsBusy
                ? Loc.T("create_repo.in_progress")
                : Loc.T("setup_wizard.finish");

    public string StepInfo => Loc.F("setup_wizard.step_info", _index + 1, _steps.Length);

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (_errorMessage == value)
                return;

            _errorMessage = value;
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
                return;

            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(NextButtonText));
            OnPropertyChanged(nameof(ShowScanSection));
            OnPropertyChanged(nameof(ShowStepContent));
            BackCommand.RaiseCanExecuteChanged();
            NextCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        private set
        {
            if (_isCompleted == value)
                return;

            _isCompleted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(NextButtonText));
            OnPropertyChanged(nameof(ShowScanSection));
            OnPropertyChanged(nameof(ShowStepContent));
            BackCommand.RaiseCanExecuteChanged();
            NextCommand.RaiseCanExecuteChanged();
        }
    }

    public int ProgressPercent
    {
        get => _progressPercent;
        private set
        {
            if (_progressPercent == value)
                return;

            _progressPercent = value;
            OnPropertyChanged();
        }
    }

    public string ProgressMessage
    {
        get => _progressMessage;
        private set
        {
            if (_progressMessage == value)
                return;

            _progressMessage = value;
            OnPropertyChanged();
        }
    }

    public int FilesProcessed
    {
        get => _filesProcessed;
        private set
        {
            if (_filesProcessed == value)
                return;

            _filesProcessed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FilesFoundCount));
        }
    }

    public int FilesTotal
    {
        get => _filesTotal;
        private set
        {
            if (_filesTotal == value)
                return;

            _filesTotal = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FilesFoundCount));
        }
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set
        {
            if (_isProgressIndeterminate == value)
                return;

            _isProgressIndeterminate = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void RefreshStepState()
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(TrackedFolderCount));
        OnPropertyChanged(nameof(SelectedFormatCount));
        BackCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
    }

    private void ResetProgressState()
    {
        IsCompleted = false;
        ProgressPercent = 0;
        ProgressMessage = Loc.T("create_repo.waiting_start");
        FilesProcessed = 0;
        FilesTotal = 0;
        IsProgressIndeterminate = false;
        _lastProgressLogSignature = null;
        ProgressLogItems.Clear();
        OnPropertyChanged(nameof(HasProgressLog));
        OnPropertyChanged(nameof(ShowScanSection));
        OnPropertyChanged(nameof(ShowStepContent));
    }

    private void Back()
    {
        if (!CanGoBack)
            return;

        _index--;
        ErrorMessage = null;
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextButtonText));
        OnPropertyChanged(nameof(StepInfo));
        OnPropertyChanged(nameof(ShowScanSection));
        OnPropertyChanged(nameof(ShowStepContent));
        RefreshStepState();
    }

    private async Task NextAsync()
    {
        ErrorMessage = null;

        if (CurrentStep is SelectDirectoriesViewModel)
        {
            if (!await _dirsVm.CommitAsync())
                return;

            GoForward();
            return;
        }

        if (CurrentStep is SelectFormatsViewModel)
        {
            if (!await _formatsVm.CommitAsync())
                return;

            GoForward();
            return;
        }

        if (CurrentStep is RepositoryNameViewModel)
        {
            if (IsCompleted)
            {
                RequestClose?.Invoke(this, EventArgs.Empty);
                return;
            }

            await FinalSaveAsync();
        }
    }

    private void GoForward()
    {
        _index++;
        ErrorMessage = null;
        ResetProgressState();
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextButtonText));
        OnPropertyChanged(nameof(StepInfo));
        OnPropertyChanged(nameof(ShowScanSection));
        OnPropertyChanged(nameof(ShowStepContent));
        RefreshStepState();
    }

    private async Task FinalSaveAsync()
    {
        try
        {
            var dirs = _dirsVm.GetSelectedDirectories();
            var exts = _formatsVm.GetSelectedExtensions();
            var repoName = _repoNameVm.RepositoryName.Trim();
            _log.LogInformation(
                "Setup wizard final save started. RepositoryName {RepositoryName}. Directories {DirectoryCount}. Formats {FormatCount}",
                repoName,
                dirs.Count,
                exts.Count);

            ResetProgressState();
            IsBusy = true;
            ProgressMessage = Loc.T("create_repo.progress_start");
            IsProgressIndeterminate = true;
            AppendProgressLog(ProgressMessage, 0);
            await Task.Yield();

            var progress = new Progress<RepositoryCreationProgressDto>(p =>
            {
                var nextPercent = Math.Clamp(p.Percent, 0, 100);
                if (nextPercent < ProgressPercent)
                    nextPercent = ProgressPercent;

                ProgressPercent = nextPercent;
                ProgressMessage = p.Message;
                FilesProcessed = p.FilesProcessed;
                FilesTotal = p.FilesTotal;
                IsProgressIndeterminate = p.FilesTotal <= 0 && p.Percent < 100;
                AppendProgressLog(p.Message, Math.Max(p.FilesProcessed, p.FilesTotal));
            });

            var result = await _mediator.Send(new SaveInitialSetupCommand(dirs, exts, repoName, progress));

            if (!result.Success)
            {
                _log.LogWarning(
                    "Setup wizard final save failed. RepositoryName {RepositoryName}. Error {Error}",
                    repoName,
                    result.Error);
                ErrorMessage = result.Error ?? Loc.T("setup_wizard.save_failed");
                ProgressMessage = Loc.T("create_repo.error_progress_label");
                IsProgressIndeterminate = false;
                AppendProgressLog(ErrorMessage, FilesFoundCount);
                return;
            }

            ProgressPercent = 100;
            ProgressMessage = Loc.T("create_repo.success_progress_label");
            IsProgressIndeterminate = false;
            IsCompleted = true;
            AppendProgressLog(Loc.T("create_repo.success_progress_label"), FilesFoundCount);

            _log.LogInformation(
                "Setup wizard final save finished successfully. RepositoryName {RepositoryName}. Directories {DirectoryCount}. Formats {FormatCount}. FilesFound {FilesFound}",
                repoName,
                dirs.Count,
                exts.Count,
                FilesFoundCount);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to save initial setup");
            ErrorMessage = Loc.F("setup_wizard.error_with_message", ex.Message);
            ProgressMessage = Loc.T("create_repo.error_progress_label");
            IsProgressIndeterminate = false;
            AppendProgressLog(ErrorMessage, FilesFoundCount);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AppendProgressLog(string? message, int filesFound)
    {
        var trimmed = (message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return;

        var signature = filesFound > 0 ? $"{trimmed}|{filesFound}" : trimmed;
        if (string.Equals(_lastProgressLogSignature, signature, StringComparison.Ordinal))
            return;

        _lastProgressLogSignature = signature;

        var finalMessage = filesFound > 0
            ? $"{trimmed} - {filesFound} {Loc.T("create_repo.files_found_suffix")}"
            : trimmed;

        ProgressLogItems.Add(new SetupWizardProgressLogItemViewModel(
            DateTime.Now.ToString("HH:mm:ss"),
            finalMessage));

        while (ProgressLogItems.Count > 120)
            ProgressLogItems.RemoveAt(0);

        OnPropertyChanged(nameof(HasProgressLog));
        OnPropertyChanged(nameof(ShowScanSection));
        OnPropertyChanged(nameof(ShowStepContent));
    }
}
