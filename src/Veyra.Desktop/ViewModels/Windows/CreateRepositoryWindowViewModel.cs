using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Repository;
using Veyra.Application.DTOs;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Navigation;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class RepositoryFormatOptionViewModel : ObservableObject
{
    public RepositoryFormatOptionViewModel(string format, bool isSelected = true)
    {
        Format = NormalizeFormat(format);
        _isSelected = isSelected;
    }

    public string Format { get; }

    [ObservableProperty] private bool _isSelected;

    public static string NormalizeFormat(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var v = value.Trim();
        if (!v.StartsWith('.'))
            v = "." + v;

        return v.ToLowerInvariant();
    }
}

public sealed record RepositoryCreationLogItemViewModel(string TimestampText, string Message);

public sealed partial class CreateRepositoryWindowViewModel : ObservableObject
{
    private readonly IMediator _mediator;
    private readonly IWindowService _windows;
    private readonly ILogger<CreateRepositoryWindowViewModel> _log;

    private int _stepIndex;
    private string? _lastProgressLogSignature;

    public event Action? RequestClose;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isCompleted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    private string? _errorMessage;

    [ObservableProperty] private string _repositoryName = string.Empty;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string _directoryPath = string.Empty;
    [ObservableProperty] private string _customFormat = string.Empty;

    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private string _progressMessage = Loc.T("create_repo.waiting_start");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesProgressLabel))]
    [NotifyPropertyChangedFor(nameof(FilesFoundCount))]
    private int _filesProcessed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesProgressLabel))]
    [NotifyPropertyChangedFor(nameof(FilesFoundCount))]
    private int _filesTotal;

    [ObservableProperty] private bool _isProgressIndeterminate;

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public int FilesFoundCount => Math.Max(FilesProcessed, FilesTotal);
    public string FilesProgressLabel => FilesFoundCount.ToString();
    public int SelectedFormatCount => Formats.Count(f => f.IsSelected);
    public int TrackedFolderCount => string.IsNullOrWhiteSpace(DirectoryPath) ? 0 : 1;
    public bool HasProgressLog => ProgressLogItems.Count > 0;

    public ObservableCollection<RepositoryFormatOptionViewModel> Formats { get; } = [];
    public ObservableCollection<RepositoryCreationLogItemViewModel> ProgressLogItems { get; } = [];

    public IRelayCommand BackCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand BrowseDirectoryCommand { get; }
    public IRelayCommand AddCustomFormatCommand { get; }

    public CreateRepositoryWindowViewModel(
        IMediator mediator,
        IWindowService windows,
        ILogger<CreateRepositoryWindowViewModel> log)
    {
        _mediator = mediator;
        _windows = windows;
        _log = log;

        BackCommand = new RelayCommand(Back, CanBack);
        NextCommand = new AsyncRelayCommand(NextAsync, CanNext);
        CancelCommand = new RelayCommand(Cancel);
        BrowseDirectoryCommand = new AsyncRelayCommand(BrowseDirectoryAsync, () => !IsBusy);
        AddCustomFormatCommand = new RelayCommand(AddCustomFormat, () => !IsBusy);

        LocalizationManager.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(StepInfo));
            OnPropertyChanged(nameof(NextButtonText));
        };

        AddDefaultFormats();
        SetStep(0);
    }

    public string Title => _stepIndex switch
    {
        0 => Loc.T("create_repo.title_step_repository"),
        1 => Loc.T("create_repo.title_step_formats"),
        _ => Loc.T("create_repo.title_step_scan")
    };

    public string StepInfo => Loc.F("create_repo.step_info", _stepIndex + 1, 3);

    public bool IsStepRepository => _stepIndex == 0;
    public bool IsStepFormats => _stepIndex == 1;
    public bool IsStepScan => _stepIndex == 2;

    public string NextButtonText
    {
        get
        {
            if (IsStepScan)
                return IsCompleted ? Loc.T("create_repo.done") : Loc.T("create_repo.in_progress");

            return Loc.T("setup_wizard.next");
        }
    }

    public IEnumerable<string> SelectedFormats => Formats
        .Where(f => f.IsSelected)
        .Select(f => f.Format)
        .Distinct(StringComparer.OrdinalIgnoreCase);

    private bool CanBack() => !IsBusy && _stepIndex > 0 && !IsCompleted;

    private bool CanNext()
    {
        if (IsBusy)
            return false;

        if (IsStepRepository)
            return ValidateRepositoryStep();

        if (IsStepFormats)
            return SelectedFormats.Any();

        return IsCompleted;
    }

    partial void OnRepositoryNameChanged(string value) => RefreshCommands();

    partial void OnDirectoryPathChanged(string value)
    {
        OnPropertyChanged(nameof(TrackedFolderCount));
        RefreshCommands();
    }

    partial void OnIsBusyChanged(bool value) => RefreshCommands();
    partial void OnCustomFormatChanged(string value) => RefreshCommands();

    private void RefreshCommands()
    {
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        BrowseDirectoryCommand.NotifyCanExecuteChanged();
        AddCustomFormatCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(NextButtonText));
    }

    private void Back()
    {
        if (_stepIndex <= 0)
            return;

        SetStep(_stepIndex - 1);
    }

    private async Task NextAsync()
    {
        ErrorMessage = null;

        if (IsStepRepository)
        {
            SetStep(1);
            return;
        }

        if (IsStepFormats)
        {
            SetStep(2);
            await CreateRepositoryAsync();
            return;
        }

        RequestClose?.Invoke();
    }

    private void Cancel() => RequestClose?.Invoke();

    private async Task BrowseDirectoryAsync()
    {
        Window? owner = _windows.GetActiveWindow();
        if (owner is null)
            return;

        IReadOnlyList<IStorageFolder> res = await owner.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = Loc.T("create_repo.select_directory_title"), AllowMultiple = false });

        var local = res.FirstOrDefault()?.Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(local) && Directory.Exists(local))
        {
            DirectoryPath = local;
            if (string.IsNullOrWhiteSpace(RepositoryName))
                RepositoryName = Path.GetFileName(local.TrimEnd('\\', '/'));
        }
    }

    private void AddCustomFormat()
    {
        if (IsBusy)
            return;

        var normalized = RepositoryFormatOptionViewModel.NormalizeFormat(CustomFormat);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (!Formats.Any(x => x.Format.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            var vm = CreateFormatOption(normalized, true);
            Formats.Add(vm);
        }
        else
        {
            var existing = Formats.First(x => x.Format.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            existing.IsSelected = true;
        }

        CustomFormat = string.Empty;
        OnPropertyChanged(nameof(SelectedFormatCount));
        RefreshCommands();
    }

    private async Task CreateRepositoryAsync()
    {
        try
        {
            IsBusy = true;
            IsCompleted = false;
            ErrorMessage = null;
            ProgressPercent = 0;
            ProgressMessage = Loc.T("create_repo.progress_start");
            FilesProcessed = 0;
            FilesTotal = 0;
            IsProgressIndeterminate = true;
            _lastProgressLogSignature = null;
            ProgressLogItems.Clear();
            OnPropertyChanged(nameof(HasProgressLog));
            AppendProgressLog(Loc.T("create_repo.progress_start"), 0);
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

            var result = await Task.Run(() => _mediator.Send(new CreateRepositoryWithFormatsCommand(
                RepositoryName,
                Description,
                DirectoryPath,
                SelectedFormats.ToList(),
                progress)));

            if (!result.Success)
            {
                ErrorMessage = UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "create_repo.error_create_failed");
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
            RefreshCommands();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to create repository in wizard");
            ErrorMessage = Loc.T("create_repo.error_unhandled");
            ProgressMessage = Loc.T("create_repo.error_progress_label");
            IsProgressIndeterminate = false;
            AppendProgressLog(ErrorMessage, FilesFoundCount);
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            RefreshCommands();
        }
    }

    private bool ValidateRepositoryStep()
    {
        if (string.IsNullOrWhiteSpace(RepositoryName))
            return false;

        if (string.IsNullOrWhiteSpace(DirectoryPath))
            return false;

        return Directory.Exists(DirectoryPath);
    }

    private void SetStep(int index)
    {
        _stepIndex = Math.Clamp(index, 0, 2);

        OnPropertyChanged(nameof(IsStepRepository));
        OnPropertyChanged(nameof(IsStepFormats));
        OnPropertyChanged(nameof(IsStepScan));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StepInfo));
        OnPropertyChanged(nameof(NextButtonText));

        RefreshCommands();
    }

    private void AddDefaultFormats()
    {
        foreach (var ext in new[]
                 {
                     ".docx", ".pdf", ".txt", ".rtf", ".odt", ".xlsx",
                     ".png", ".jpg", ".jpeg", ".gif", ".svg",
                     ".json", ".xml", ".cs", ".js", ".ts", ".java", ".py", ".md"
                 })
        {
            Formats.Add(CreateFormatOption(ext, true));
        }

        OnPropertyChanged(nameof(SelectedFormatCount));
    }

    private RepositoryFormatOptionViewModel CreateFormatOption(string ext, bool isSelected)
    {
        var vm = new RepositoryFormatOptionViewModel(ext, isSelected);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RepositoryFormatOptionViewModel.IsSelected))
            {
                OnPropertyChanged(nameof(SelectedFormatCount));
                RefreshCommands();
            }
        };

        return vm;
    }

    private void AppendProgressLog(string? message, int filesFound)
    {
        var trimmed = (message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return;

        var signature = filesFound > 0
            ? $"{trimmed}|{filesFound}"
            : trimmed;

        if (string.Equals(_lastProgressLogSignature, signature, StringComparison.Ordinal))
            return;

        _lastProgressLogSignature = signature;

        var finalMessage = filesFound > 0
            ? $"{trimmed} - {filesFound} {Loc.T("create_repo.files_found_suffix")}"
            : trimmed;

        ProgressLogItems.Add(new RepositoryCreationLogItemViewModel(
            DateTime.Now.ToString("HH:mm:ss"),
            finalMessage));

        while (ProgressLogItems.Count > 120)
            ProgressLogItems.RemoveAt(0);

        OnPropertyChanged(nameof(HasProgressLog));
    }
}
