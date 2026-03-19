using System;
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
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Storage;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class RepositoryBundleExportWizardWindowViewModel : ObservableObject
{
    private readonly IMediator _mediator;
    private readonly IWindowService _windows;
    private readonly ILogger<RepositoryBundleExportWizardWindowViewModel> _log;

    public event Action? RequestClose;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartExportCommand))]
    [NotifyPropertyChangedFor(nameof(CanStartExport))]
    private int _repositoryId;

    [ObservableProperty] private string _repositoryName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartExportCommand))]
    [NotifyPropertyChangedFor(nameof(CanStartExport))]
    private string _bundlePath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartExportCommand))]
    [NotifyPropertyChangedFor(nameof(CanStartExport))]
    [NotifyPropertyChangedFor(nameof(StartExportLabel))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseLabel))]
    [NotifyPropertyChangedFor(nameof(HasResultDetails))]
    private bool _isCompleted;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultSummary))]
    private string _resultSummary = string.Empty;
    [ObservableProperty] private int _snapshotCount;
    [ObservableProperty] private int _fileVersionCount;
    [ObservableProperty] private int _blockFileCount;
    [ObservableProperty] private string _bundleSizeText = string.Empty;

    public RepositoryBundleExportWizardWindowViewModel(
        IMediator mediator,
        IWindowService windows,
        ILogger<RepositoryBundleExportWizardWindowViewModel> log)
    {
        _mediator = mediator;
        _windows = windows;
        _log = log;
        LocalizationManager.Instance.LanguageChanged += (_, _) => RefreshLocalizationState();
    }

    public string WindowTitle => Loc.T("bundle_export.window_title");
    public string StepOneTitle => Loc.T("bundle_export.step_path_title");
    public string StepTwoTitle => Loc.T("bundle_export.step_run_title");
    public string StepThreeTitle => Loc.T("bundle_export.step_done_title");
    public string StepOneDescription => Loc.F("bundle_export.step_path_description", string.IsNullOrWhiteSpace(RepositoryName) ? Loc.T("common.not_available_short") : RepositoryName);
    public string StepTwoDescription => Loc.T("bundle_export.step_run_description");
    public string StepThreeDescription => Loc.T("bundle_export.step_done_description");
    public bool CanStartExport => !IsBusy && RepositoryId > 0 && !string.IsNullOrWhiteSpace(BundlePath);
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasResultSummary => !string.IsNullOrWhiteSpace(ResultSummary);
    public bool HasResultDetails => IsCompleted;
    public string StartExportLabel => IsBusy ? Loc.T("common.working") : Loc.T("bundle_export.start_button");
    public string CloseLabel => IsCompleted ? Loc.T("common.close") : Loc.T("common.cancel");

    public void Configure(int repositoryId, string repositoryName)
    {
        RepositoryId = repositoryId;
        RepositoryName = repositoryName ?? string.Empty;
        BundlePath = BuildSuggestedBundlePath();
        Message = string.Empty;
        ResultSummary = string.Empty;
        IsCompleted = false;
        SnapshotCount = 0;
        FileVersionCount = 0;
        BlockFileCount = 0;
        BundleSizeText = string.Empty;
        RefreshLocalizationState();
    }

    [RelayCommand]
    private async Task BrowseBundlePathAsync()
    {
        var owner = _windows.GetActiveWindow();
        if (owner is null)
            return;

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("repo_settings.bundle_export_title"),
            SuggestedFileName = BuildSuggestedBundleFileName(),
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType(Loc.T("repo_settings.bundle_file_type"))
                {
                    Patterns = ["*.veyra.zip", "*.veyra-bundle", "*.zip"]
                }
            ]
        });

        var localPath = StoragePathResolver.TryGetLocalPath(file);
        if (!string.IsNullOrWhiteSpace(localPath))
            BundlePath = localPath;
    }

    [RelayCommand(CanExecute = nameof(CanStartExport))]
    private async Task StartExportAsync()
    {
        if (!CanStartExport)
            return;

        try
        {
            IsBusy = true;
            Message = Loc.T("bundle_export.running");
            ResultSummary = string.Empty;
            IsCompleted = false;

            var result = await _mediator.Send(new ExportRepositoryBundleCommand(RepositoryId, BundlePath));
            if (!result.Success || result.Value is null)
            {
                Message = UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "repo_settings.error_bundle_failed");
                return;
            }

            SnapshotCount = result.Value.SnapshotCount;
            FileVersionCount = result.Value.FileVersionCount;
            BlockFileCount = result.Value.BlockFileCount;
            BundleSizeText = FormatSize(result.Value.BundleSizeBytes);
            ResultSummary = $"{result.Value.Summary}\n{Loc.T("repo_settings.bundle_path_label")}: {result.Value.BundlePath}";
            Message = Loc.T("bundle_export.completed");
            IsCompleted = true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Bundle export wizard failed. RepositoryId {RepositoryId}", RepositoryId);
            Message = Loc.T("repo_settings.error_bundle_failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke();
    }

    private string BuildSuggestedBundlePath()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var fileName = BuildSuggestedBundleFileName();
        return string.IsNullOrWhiteSpace(documents)
            ? fileName
            : Path.Combine(documents, fileName);
    }

    private string BuildSuggestedBundleFileName()
    {
        var safeName = string.IsNullOrWhiteSpace(RepositoryName)
            ? "repository"
            : string.Concat(RepositoryName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch)).Trim();

        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "repository";

        return $"{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}.veyra.zip";
    }

    private void RefreshLocalizationState()
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(StepOneTitle));
        OnPropertyChanged(nameof(StepTwoTitle));
        OnPropertyChanged(nameof(StepThreeTitle));
        OnPropertyChanged(nameof(StepOneDescription));
        OnPropertyChanged(nameof(StepTwoDescription));
        OnPropertyChanged(nameof(StepThreeDescription));
        OnPropertyChanged(nameof(CanStartExport));
        OnPropertyChanged(nameof(HasMessage));
        OnPropertyChanged(nameof(HasResultSummary));
        OnPropertyChanged(nameof(HasResultDetails));
        OnPropertyChanged(nameof(StartExportLabel));
        OnPropertyChanged(nameof(CloseLabel));
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
