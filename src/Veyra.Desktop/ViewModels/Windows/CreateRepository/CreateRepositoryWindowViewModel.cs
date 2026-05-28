using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Repository;
using Veyra.Application.Common.Files;
using Veyra.Application.DTOs;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Models.TrackedFormats;
using Veyra.Desktop.Services.Execution;
using Veyra.Desktop.Services.Maintenance;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Storage;
using Veyra.Desktop.ViewModels;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class CreateRepositoryWindowViewModel : ObservableObject
{
    private readonly IServiceScopeExecutor _scopeExecutor;
    private readonly IWindowService _windows;
    private readonly ILogger<CreateRepositoryWindowViewModel> _log;
    private readonly IRepositoryRetentionDefaultsApplier _retentionDefaultsApplier;
    private readonly List<RepositoryBusyFileDto> _retryableBusyFiles = [];

    private int _stepIndex;
    private int _createdRepositoryId;
    private string? _lastProgressLogSignature;
    private CancellationTokenSource? _activeOperationCancellation;

    public event Action? RequestClose;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isCompleted;
    [ObservableProperty] private bool _isCancelling;
    [ObservableProperty] private bool _canCancelCurrentOperation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    private string? _errorMessage;

    [ObservableProperty] private string _repositoryName = string.Empty;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string _directoryPath = string.Empty;
    [ObservableProperty] private bool _isDirectoryPathLocked;
    [ObservableProperty] private string _customFormat = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDeterminateProgressValue))]
    [NotifyPropertyChangedFor(nameof(ProgressDisplayText))]
    private int _progressPercent;
    [ObservableProperty] private string _progressMessage = Loc.T("create_repo.waiting_start");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesProgressLabel))]
    [NotifyPropertyChangedFor(nameof(FilesFoundCount))]
    [NotifyPropertyChangedFor(nameof(TrackedFilesCount))]
    private int _filesProcessed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesProgressLabel))]
    [NotifyPropertyChangedFor(nameof(FilesFoundCount))]
    [NotifyPropertyChangedFor(nameof(TrackedFilesCount))]
    private int _filesTotal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDeterminateProgressValue))]
    [NotifyPropertyChangedFor(nameof(ProgressDisplayText))]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesFoundCount))]
    private int _directoryFilesFound;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrackedFilesCount))]
    private int _trackedFilesPreviewCount;

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public int FilesFoundCount => DirectoryFilesFound > 0 ? DirectoryFilesFound : Math.Max(FilesProcessed, FilesTotal);
    public string FilesProgressLabel => FilesFoundCount.ToString();
    public int TrackedFilesCount => Math.Max(TrackedFilesPreviewCount, Math.Max(FilesProcessed, FilesTotal));
    public int SelectedFormatCount => Formats.Count(f => f.IsSelected);
    public int TrackedFolderCount => string.IsNullOrWhiteSpace(DirectoryPath) ? 0 : 1;
    public bool HasProgressLog => ProgressLogItems.Count > 0;
    public bool CanBrowseDirectory => !IsBusy && !IsDirectoryPathLocked;
    public bool ShowDeterminateProgressValue => ProgressPercent > 0;
    public bool ShowAnimatedActivity => IsBusy;
    public bool ShowCancelButton => !IsCompleted && (!IsBusy || CanCancelCurrentOperation);
    public bool ShowBusyCancelButton => IsBusy && CanCancelCurrentOperation;
    public bool CanCancelOperation => !IsCancelling && (!IsBusy || CanCancelCurrentOperation);
    public string ProgressDisplayText => ShowDeterminateProgressValue
        ? $"{ProgressPercent}%"
        : Loc.T("create_repo.in_progress");
    public bool ShowCreateSuccessBanner => IsCompleted && !HasRetryableBusyFiles;
    public bool HasRetryableBusyFiles => _retryableBusyFiles.Count > 0;
    public bool HasRetryableBusyFilesPreview => !string.IsNullOrWhiteSpace(RetryableBusyFilesPreview);
    public string RetryableBusyFilesSummary => HasRetryableBusyFiles
        ? Loc.F("create_repo.warning_busy_files_body", _retryableBusyFiles.Count)
        : string.Empty;
    public string RetryableBusyFilesPreview => BuildBusyFilesPreview();

    public ObservableCollection<RepositoryFormatOptionViewModel> Formats { get; } = [];
    public ObservableCollection<FormatCategoryItemViewModel> FormatCategories { get; } = [];
    public ObservableCollection<RepositoryCreationLogItemViewModel> ProgressLogItems { get; } = [];

    public IRelayCommand BackCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand BrowseDirectoryCommand { get; }
    public IRelayCommand AddCustomFormatCommand { get; }
    public IRelayCommand<FormatCategoryItemViewModel?> ToggleFormatCategoryCommand { get; }
    public IAsyncRelayCommand RetryUnavailableFilesCommand { get; }

    public CreateRepositoryWindowViewModel(
        IServiceScopeExecutor scopeExecutor,
        IWindowService windows,
        IRepositoryRetentionDefaultsApplier retentionDefaultsApplier,
        ILogger<CreateRepositoryWindowViewModel> log)
    {
        _scopeExecutor = scopeExecutor;
        _windows = windows;
        _retentionDefaultsApplier = retentionDefaultsApplier;
        _log = log;

        BackCommand = new RelayCommand(Back, CanBack);
        NextCommand = new AsyncRelayCommand(NextAsync, CanNext);
        CancelCommand = new RelayCommand(Cancel);
        BrowseDirectoryCommand = new AsyncRelayCommand(BrowseDirectoryAsync, () => CanBrowseDirectory);
        AddCustomFormatCommand = new RelayCommand(AddCustomFormat, () => !IsBusy);
        ToggleFormatCategoryCommand = new RelayCommand<FormatCategoryItemViewModel?>(ToggleFormatCategory, _ => !IsBusy);
        RetryUnavailableFilesCommand = new AsyncRelayCommand(RetryUnavailableFilesAsync, CanRetryUnavailableFiles);

        LocalizationManager.Instance.LanguageChanged += (_, _) =>
        {
            foreach (var category in FormatCategories)
                category.RefreshLocalization();
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(StepInfo));
            OnPropertyChanged(nameof(NextButtonText));
            OnPropertyChanged(nameof(CancelButtonText));
        };

        AddDefaultFormats();
        BuildFormatCategories();
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

    public string CancelButtonText => IsBusy
        ? Loc.T("common.cancel")
        : Loc.T("common.cancel");

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

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowAnimatedActivity));
        OnPropertyChanged(nameof(CanBrowseDirectory));
        OnPropertyChanged(nameof(CancelButtonText));
        OnPropertyChanged(nameof(ShowCancelButton));
        OnPropertyChanged(nameof(ShowBusyCancelButton));
        OnPropertyChanged(nameof(CanCancelOperation));
        RefreshCommands();
    }

    partial void OnIsCompletedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCreateSuccessBanner));
        OnPropertyChanged(nameof(ShowCancelButton));
    }

    partial void OnIsCancellingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancelOperation));
        RefreshCommands();
    }

    partial void OnCanCancelCurrentOperationChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCancelButton));
        OnPropertyChanged(nameof(ShowBusyCancelButton));
        OnPropertyChanged(nameof(CanCancelOperation));
        RefreshCommands();
    }

    partial void OnIsDirectoryPathLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanBrowseDirectory));
        RefreshCommands();
    }

    public void ConfigureInitialDirectory(string directoryPath, bool lockDirectory = true)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            return;

        DirectoryPath = directoryPath;
        IsDirectoryPathLocked = lockDirectory;

        if (string.IsNullOrWhiteSpace(RepositoryName))
        {
            var suggestedName = Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            RepositoryName = string.IsNullOrWhiteSpace(suggestedName)
                ? directoryPath
                : suggestedName;
        }
    }
    partial void OnCustomFormatChanged(string value) => RefreshCommands();

    private void RefreshCommands()
    {
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        BrowseDirectoryCommand.NotifyCanExecuteChanged();
        AddCustomFormatCommand.NotifyCanExecuteChanged();
        ToggleFormatCategoryCommand.NotifyCanExecuteChanged();
        RetryUnavailableFilesCommand.NotifyCanExecuteChanged();
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

    private void Cancel()
    {
        if (IsBusy)
        {
            if (!CanCancelCurrentOperation)
                return;

            if (IsCancelling)
                return;

            IsCancelling = true;
            ProgressMessage = Loc.T("create_repo.cancel_requested");
            AppendProgressLog(Loc.T("create_repo.cancel_requested"), 0);
            _activeOperationCancellation?.Cancel();
            return;
        }

        RequestClose?.Invoke();
    }

    public bool RequestWindowClose()
    {
        if (!IsBusy)
            return true;

        Cancel();
        return false;
    }

    private async Task BrowseDirectoryAsync()
    {
        Window? owner = _windows.GetActiveWindow();
        if (owner is null)
            return;

        IReadOnlyList<IStorageFolder> res = await owner.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = Loc.T("create_repo.select_directory_title"), AllowMultiple = false });

        var local = StoragePathResolver.TryGetLocalPath(res.FirstOrDefault());
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

    private void ToggleFormatCategory(FormatCategoryItemViewModel? category)
    {
        if (IsBusy || category is null)
            return;

        category.IsApplied = !category.IsApplied;

        if (category.IsApplied)
        {
            foreach (var format in category.Formats)
            {
                var normalized = RepositoryFormatOptionViewModel.NormalizeFormat(format);
                var existing = Formats.FirstOrDefault(x => x.Format.Equals(normalized, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    existing = CreateFormatOption(normalized, true);
                    Formats.Add(existing);
                }

                existing.IsSelected = true;
            }
        }
        else
        {
            var protectedFormats = FormatCategories
                .Where(item => !ReferenceEquals(item, category) && item.IsApplied)
                .SelectMany(item => item.Formats)
                .Select(RepositoryFormatOptionViewModel.NormalizeFormat)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var format in category.Formats.Select(RepositoryFormatOptionViewModel.NormalizeFormat))
            {
                if (protectedFormats.Contains(format))
                    continue;

                var existing = Formats.FirstOrDefault(x => x.Format.Equals(format, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                    existing.IsSelected = false;
            }
        }

        OnPropertyChanged(nameof(SelectedFormatCount));
        RefreshCommands();
    }

    private async Task CreateRepositoryAsync()
    {
        try
        {
            IsBusy = true;
            IsCompleted = false;
            IsCancelling = false;
            CanCancelCurrentOperation = true;
            ErrorMessage = null;
            _createdRepositoryId = 0;
            ProgressPercent = 0;
            ProgressMessage = Loc.T("create_repo.progress_start");
            FilesProcessed = 0;
            FilesTotal = 0;
            DirectoryFilesFound = 0;
            TrackedFilesPreviewCount = 0;
            IsProgressIndeterminate = true;
            _lastProgressLogSignature = null;
            SetRetryableBusyFiles(Array.Empty<RepositoryBusyFileDto>());
            ProgressLogItems.Clear();
            OnPropertyChanged(nameof(HasProgressLog));
            AppendProgressLog(Loc.T("create_repo.progress_start"), 0);
            await Task.Yield();

            using var operationCancellation = new CancellationTokenSource();
            _activeOperationCancellation = operationCancellation;
            var operationToken = operationCancellation.Token;

            ProgressMessage = Loc.T("create_repo.progress_counting_files");
            var previewMetrics = await CountDirectoryMetricsAsync(DirectoryPath, SelectedFormats.ToArray(), operationToken);
            DirectoryFilesFound = previewMetrics.TotalFiles;
            TrackedFilesPreviewCount = previewMetrics.TrackedFiles;
            AppendProgressLog(
                Loc.F("create_repo.progress_counting_files_result", previewMetrics.TotalFiles, previewMetrics.TrackedFiles),
                0);

            var progress = new Progress<RepositoryCreationProgressDto>(ApplyCreationProgress);

            var command = new CreateRepositoryWithFormatsCommand(
                RepositoryName,
                Description,
                DirectoryPath,
                SelectedFormats.ToList(),
                progress);

            var result = await _scopeExecutor.ExecuteAsync<IMediator, Veyra.Application.Common.Results.OperationResult<RepositoryCreationOutcomeDto>>(
                (mediator, token) => mediator.Send(command, token),
                operationToken);
            CanCancelCurrentOperation = false;

            if (!result.Success)
            {
                ErrorMessage = UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "create_repo.error_create_failed");
                ProgressMessage = Loc.T("create_repo.error_progress_label");
                IsProgressIndeterminate = false;
                AppendProgressLog(ErrorMessage, FilesFoundCount);
                return;
            }

            if (result.Value is null || result.Value.RepositoryId <= 0)
            {
                ErrorMessage = Loc.T("create_repo.error_create_failed");
                ProgressMessage = Loc.T("create_repo.error_progress_label");
                IsProgressIndeterminate = false;
                AppendProgressLog(ErrorMessage, FilesFoundCount);
                return;
            }

            _createdRepositoryId = result.Value.RepositoryId;
            operationToken.ThrowIfCancellationRequested();
            await _retentionDefaultsApplier.ApplyToRepositoryAsync(_createdRepositoryId);
            operationToken.ThrowIfCancellationRequested();
            SetRetryableBusyFiles(result.Value.BusyFilesSafe);

            ProgressPercent = 100;
            ProgressMessage = result.Value.HasBusyFiles
                ? Loc.F("create_repo.warning_busy_files_progress_label", result.Value.BusyFilesCount)
                : Loc.T("create_repo.success_progress_label");
            IsProgressIndeterminate = false;
            IsCompleted = true;
            AppendProgressLog(
                result.Value.HasBusyFiles
                    ? Loc.F("create_repo.warning_busy_files_log", result.Value.BusyFilesCount)
                    : Loc.T("create_repo.success_progress_label"),
                FilesFoundCount);
            RefreshCommands();
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = Loc.T("create_repo.cancelled");
            ProgressMessage = Loc.T("create_repo.cancelled");
            IsProgressIndeterminate = false;
            AppendProgressLog(Loc.T("create_repo.cancelled"), 0);
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
            if (_activeOperationCancellation is not null)
            {
                _activeOperationCancellation.Dispose();
                _activeOperationCancellation = null;
            }

            IsBusy = false;
            IsCancelling = false;
            CanCancelCurrentOperation = false;
            IsProgressIndeterminate = false;
            RefreshCommands();
        }
    }

    private async Task RetryUnavailableFilesAsync()
    {
        if (!CanRetryUnavailableFiles())
            return;

        try
        {
            IsBusy = true;
            IsCompleted = false;
            IsCancelling = false;
            CanCancelCurrentOperation = true;
            ErrorMessage = null;
            ProgressPercent = 0;
            ProgressMessage = Loc.T("create_repo.retry_start");
            IsProgressIndeterminate = true;
            AppendProgressLog(Loc.T("create_repo.retry_start"), FilesFoundCount);
            await Task.Yield();

            using var operationCancellation = new CancellationTokenSource();
            _activeOperationCancellation = operationCancellation;
            var operationToken = operationCancellation.Token;

            var progress = new Progress<RepositoryScanProgressDto>(p =>
                ApplyCreationProgress(new RepositoryCreationProgressDto(
                    p.Stage,
                    p.Percent,
                    p.FilesProcessed,
                    p.FilesTotal,
                    p.Message)));

            var command = new ScanRepositoryCommand(
                _createdRepositoryId,
                progress,
                new RepositoryScanOptionsDto(
                    SaveFileVersions: true,
                    TriggerOverride: "retry_busy_files"));

            var result = await _scopeExecutor.ExecuteAsync<IMediator, Veyra.Application.Common.Results.OperationResult<RepositoryScanResultDto>>(
                (mediator, token) => mediator.Send(command, token),
                operationToken);
            CanCancelCurrentOperation = false;

            if (!result.Success || result.Value is null)
            {
                ErrorMessage = UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "create_repo.error_create_failed");
                ProgressMessage = Loc.T("create_repo.error_progress_label");
                IsProgressIndeterminate = false;
                AppendProgressLog(ErrorMessage, FilesFoundCount);
                return;
            }

            SetRetryableBusyFiles(result.Value.BusyFilesSafe);
            ProgressPercent = 100;
            ProgressMessage = result.Value.HasBusyFiles
                ? Loc.F("create_repo.warning_busy_files_progress_label", result.Value.BusyFilesCount)
                : Loc.T("create_repo.retry_success_progress_label");
            IsProgressIndeterminate = false;
            IsCompleted = true;
            AppendProgressLog(
                result.Value.HasBusyFiles
                    ? Loc.F("create_repo.warning_busy_files_log", result.Value.BusyFilesCount)
                    : Loc.T("create_repo.retry_success_progress_label"),
                FilesFoundCount);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = Loc.T("create_repo.cancelled");
            ProgressMessage = Loc.T("create_repo.cancelled");
            IsProgressIndeterminate = false;
            AppendProgressLog(Loc.T("create_repo.cancelled"), 0);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to retry unavailable files for repository {RepositoryId}", _createdRepositoryId);
            ErrorMessage = Loc.T("create_repo.error_unhandled");
            ProgressMessage = Loc.T("create_repo.error_progress_label");
            IsProgressIndeterminate = false;
            AppendProgressLog(ErrorMessage, FilesFoundCount);
        }
        finally
        {
            if (_activeOperationCancellation is not null)
            {
                _activeOperationCancellation.Dispose();
                _activeOperationCancellation = null;
            }

            IsBusy = false;
            IsCancelling = false;
            CanCancelCurrentOperation = false;
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
        foreach (var ext in TrackedFormatCategoryCatalog.All
                     .SelectMany(category => category.Formats)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Formats.Add(CreateFormatOption(ext, false));
        }

        OnPropertyChanged(nameof(SelectedFormatCount));
    }

    private void BuildFormatCategories()
    {
        FormatCategories.Clear();
        foreach (var definition in TrackedFormatCategoryCatalog.All)
            FormatCategories.Add(new FormatCategoryItemViewModel(definition));
    }

    private RepositoryFormatOptionViewModel CreateFormatOption(string ext, bool isSelected)
    {
        var vm = new RepositoryFormatOptionViewModel(ext, isSelected);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RepositoryFormatOptionViewModel.IsSelected))
            {
                RefreshFormatCategoryState();
                OnPropertyChanged(nameof(SelectedFormatCount));
                RefreshCommands();
            }
        };

        return vm;
    }

    private void RefreshFormatCategoryState()
    {
        var selected = Formats
            .Where(format => format.IsSelected)
            .Select(format => format.Format)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var category in FormatCategories)
        {
            var shouldBeApplied = category.Formats
                .Select(RepositoryFormatOptionViewModel.NormalizeFormat)
                .All(selected.Contains);

            if (category.IsApplied != shouldBeApplied)
            category.IsApplied = shouldBeApplied;
        }
    }

    private static bool TryGetReliableProgressPercent(RepositoryCreationProgressDto progress, out int percent)
    {
        if (string.Equals(progress.Stage, "done", StringComparison.OrdinalIgnoreCase))
        {
            percent = 100;
            return true;
        }

        if (progress.Percent > 0)
        {
            percent = Math.Clamp(progress.Percent, 1, 99);
            return true;
        }

        if (progress.FilesTotal > 0
            && progress.FilesProcessed > 0
            && progress.FilesProcessed < progress.FilesTotal)
        {
            percent = Math.Clamp(
                (int)Math.Round((double)progress.FilesProcessed / progress.FilesTotal * 100.0),
                1,
                99);
            return true;
        }

        percent = 0;
        return false;
    }

    private bool CanRetryUnavailableFiles()
        => !IsBusy && _createdRepositoryId > 0 && HasRetryableBusyFiles;

    private void ApplyCreationProgress(RepositoryCreationProgressDto p)
    {
        var message = RepositoryCreationProgressText.Format(p);
        ProgressMessage = message;
        FilesProcessed = p.FilesProcessed;
        FilesTotal = p.FilesTotal;

        var trackedFiles = Math.Max(p.FilesProcessed, p.FilesTotal);
        if (trackedFiles > TrackedFilesPreviewCount)
            TrackedFilesPreviewCount = trackedFiles;

        if (TryGetReliableProgressPercent(p, out var nextPercent))
        {
            if (nextPercent < ProgressPercent)
                nextPercent = ProgressPercent;

            ProgressPercent = nextPercent;
            IsProgressIndeterminate = false;
        }
        else
        {
            IsProgressIndeterminate = true;
        }

        AppendProgressLog(message, 0);
    }

    private void SetRetryableBusyFiles(IReadOnlyCollection<RepositoryBusyFileDto> busyFiles)
    {
        _retryableBusyFiles.Clear();
        _retryableBusyFiles.AddRange(busyFiles);
        OnPropertyChanged(nameof(HasRetryableBusyFiles));
        OnPropertyChanged(nameof(HasRetryableBusyFilesPreview));
        OnPropertyChanged(nameof(RetryableBusyFilesSummary));
        OnPropertyChanged(nameof(RetryableBusyFilesPreview));
        OnPropertyChanged(nameof(ShowCreateSuccessBanner));
        RefreshCommands();
    }

    private string BuildBusyFilesPreview()
    {
        if (_retryableBusyFiles.Count == 0)
            return string.Empty;

        var preview = _retryableBusyFiles
            .Take(3)
            .Select(file => file.FullPath)
            .ToList();

        if (_retryableBusyFiles.Count > preview.Count)
            preview.Add(Loc.F("create_repo.warning_busy_files_more", _retryableBusyFiles.Count - preview.Count));

        return string.Join(Environment.NewLine, preview);
    }

    private static async Task<(int TotalFiles, int TrackedFiles)> CountDirectoryMetricsAsync(
        string rootPath,
        IReadOnlyCollection<string> selectedFormats,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return (0, 0);

        var trackedFormats = selectedFormats
            .Where(format => !string.IsNullOrWhiteSpace(format))
            .Select(RepositoryFormatOptionViewModel.NormalizeFormat)
            .Where(static format => !string.IsNullOrWhiteSpace(format))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return await Task.Run(() =>
        {
            var totalFiles = 0;
            var trackedFiles = 0;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(rootPath));

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var current = stack.Pop();

                IEnumerable<DirectoryInfo> directories;
                try
                {
                    directories = current.EnumerateDirectories();
                }
                catch
                {
                    continue;
                }

                foreach (var directory in directories)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue;
                    }
                    catch
                    {
                        continue;
                    }

                    stack.Push(directory);
                }

                IEnumerable<FileInfo> files;
                try
                {
                    files = current.EnumerateFiles();
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    totalFiles++;
                    var ext = KnownFileExtensions.NormalizeTrackedFileFormat(file.Extension);
                    if (trackedFormats.Count == 0 || trackedFormats.Contains(ext))
                        trackedFiles++;
                }
            }

            return (totalFiles, trackedFiles);
        }, ct);
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
