using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Repository;
using Veyra.Application.Queries.Repository;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Navigation;
using Veyra.Desktop.Services.Storage;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class RepositoryBundleImportWizardWindowViewModel : ObservableObject
{
    private readonly IMediator _mediator;
    private readonly IWindowService _windows;
    private readonly ILogger<RepositoryBundleImportWizardWindowViewModel> _log;

    public event Action? RequestClose;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartImportCommand))]
    [NotifyPropertyChangedFor(nameof(CanStartImport))]
    private string _bundlePath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartImportCommand))]
    [NotifyPropertyChangedFor(nameof(CanStartImport))]
    private string _targetDirectoryPath = string.Empty;

    [ObservableProperty] private string _repositoryNameOverride = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartImportCommand))]
    [NotifyPropertyChangedFor(nameof(CanStartImport))]
    [NotifyPropertyChangedFor(nameof(StartImportLabel))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartImportCommand))]
    [NotifyPropertyChangedFor(nameof(CanStartImport))]
    private bool _isBundleValidated;
    [ObservableProperty] private int _bundleFormatVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    private string _validationMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseLabel))]
    private bool _isCompleted;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultSummary))]
    private string _resultSummary = string.Empty;
    [ObservableProperty] private int _importedRepositoryId;

    public ObservableCollection<string> ValidationWarnings { get; } = [];
    public ObservableCollection<string> ImportWarnings { get; } = [];

    public RepositoryBundleImportWizardWindowViewModel(
        IMediator mediator,
        IWindowService windows,
        ILogger<RepositoryBundleImportWizardWindowViewModel> log)
    {
        _mediator = mediator;
        _windows = windows;
        _log = log;
        ValidationWarnings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasValidationWarnings));
        ImportWarnings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasImportWarnings));
        LocalizationManager.Instance.LanguageChanged += (_, _) => RefreshLocalizationState();
    }

    public string WindowTitle => Loc.T("bundle_import.window_title");
    public string StepOneTitle => Loc.T("bundle_import.step_bundle_title");
    public string StepTwoTitle => Loc.T("bundle_import.step_target_title");
    public string StepThreeTitle => Loc.T("bundle_import.step_done_title");
    public string StepOneDescription => Loc.T("bundle_import.step_bundle_description");
    public string StepTwoDescription => Loc.T("bundle_import.step_target_description");
    public string StepThreeDescription => Loc.T("bundle_import.step_done_description");
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);
    public bool HasValidationWarnings => ValidationWarnings.Count > 0;
    public bool HasImportWarnings => ImportWarnings.Count > 0;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasResultSummary => !string.IsNullOrWhiteSpace(ResultSummary);
    public bool CanStartImport => !IsBusy && IsBundleValidated && !string.IsNullOrWhiteSpace(TargetDirectoryPath);
    public string StartImportLabel => IsBusy ? Loc.T("common.working") : Loc.T("bundle_import.start_button");
    public string CloseLabel => IsCompleted ? Loc.T("common.close") : Loc.T("common.cancel");

    public void Configure(string suggestedTargetDirectory)
    {
        BundlePath = string.Empty;
        TargetDirectoryPath = suggestedTargetDirectory ?? string.Empty;
        RepositoryNameOverride = string.Empty;
        IsBundleValidated = false;
        BundleFormatVersion = 0;
        ValidationMessage = string.Empty;
        Message = string.Empty;
        ResultSummary = string.Empty;
        IsCompleted = false;
        ImportedRepositoryId = 0;
        ValidationWarnings.Clear();
        ImportWarnings.Clear();
        RefreshLocalizationState();
    }

    [RelayCommand]
    private async Task BrowseBundlePathAsync()
    {
        var owner = _windows.GetActiveWindow();
        if (owner is null)
            return;

        var selection = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("repo_settings.bundle_import_title"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.T("repo_settings.bundle_file_type"))
                {
                    Patterns = ["*.veyra.zip", "*.veyra-bundle", "*.zip"]
                }
            ]
        });

        var localPath = StoragePathResolver.TryGetLocalPath(selection.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(localPath))
            return;

        BundlePath = localPath;
        await ValidateBundleAsync();
    }

    [RelayCommand]
    private async Task BrowseTargetDirectoryAsync()
    {
        var owner = _windows.GetActiveWindow();
        if (owner is null)
            return;

        var selection = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.T("repo_settings.bundle_import_target_title"),
            AllowMultiple = false
        });

        var localPath = StoragePathResolver.TryGetLocalPath(selection.FirstOrDefault());
        if (!string.IsNullOrWhiteSpace(localPath))
            TargetDirectoryPath = localPath;
    }

    [RelayCommand(CanExecute = nameof(CanStartImport))]
    private async Task StartImportAsync()
    {
        if (!CanStartImport)
            return;

        try
        {
            IsBusy = true;
            Message = Loc.T("bundle_import.running");
            ResultSummary = string.Empty;
            ImportWarnings.Clear();
            IsCompleted = false;

            var result = await _mediator.Send(new ImportRepositoryBundleCommand(
                BundlePath,
                TargetDirectoryPath,
                string.IsNullOrWhiteSpace(RepositoryNameOverride) ? null : RepositoryNameOverride.Trim()));

            if (!result.Success || result.Value is null)
            {
                Message = UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "repo_settings.error_bundle_failed");
                return;
            }

            ImportedRepositoryId = result.Value.RepositoryId;
            foreach (var warning in result.Value.Warnings)
                ImportWarnings.Add(warning);

            ResultSummary =
                $"{result.Value.Summary}\n{Loc.T("repo_settings.bundle_imported_repository_id")}: {result.Value.RepositoryId}\n{Loc.T("common.path")}: {result.Value.TargetDirectoryPath}";
            Message = Loc.T("bundle_import.completed");
            IsCompleted = true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Bundle import wizard failed. Bundle {BundlePath}", BundlePath);
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

    private async Task ValidateBundleAsync()
    {
        IsBundleValidated = false;
        BundleFormatVersion = 0;
        ValidationMessage = string.Empty;
        ValidationWarnings.Clear();
        ResultSummary = string.Empty;
        ImportWarnings.Clear();
        IsCompleted = false;

        if (string.IsNullOrWhiteSpace(BundlePath))
            return;

        try
        {
            var validation = await _mediator.Send(new ValidateRepositoryBundleQuery(BundlePath));
            BundleFormatVersion = validation.BundleFormatVersion;
            ValidationMessage = UserFacingMessageLocalizer.LocalizeOrFallback(validation.Message, "repo_settings.bundle_validation_failed");
            foreach (var warning in validation.Warnings)
                ValidationWarnings.Add(warning);

            IsBundleValidated = validation.IsValid;
            if (string.IsNullOrWhiteSpace(RepositoryNameOverride))
                RepositoryNameOverride = Path.GetFileNameWithoutExtension(BundlePath)?.Replace(".veyra", string.Empty, StringComparison.OrdinalIgnoreCase) ?? string.Empty;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Bundle validation failed. Bundle {BundlePath}", BundlePath);
            ValidationMessage = Loc.T("repo_settings.bundle_validation_failed");
            IsBundleValidated = false;
        }
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
        OnPropertyChanged(nameof(HasValidationMessage));
        OnPropertyChanged(nameof(HasValidationWarnings));
        OnPropertyChanged(nameof(HasImportWarnings));
        OnPropertyChanged(nameof(HasMessage));
        OnPropertyChanged(nameof(HasResultSummary));
        OnPropertyChanged(nameof(CanStartImport));
        OnPropertyChanged(nameof(StartImportLabel));
        OnPropertyChanged(nameof(CloseLabel));
    }
}
