using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class SnapshotNameDialogWindowViewModel : ObservableObject
{
    public event Action<bool>? RequestClose;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _snapshotName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedChangedFile))]
    [NotifyPropertyChangedFor(nameof(SelectedChangedFileTitle))]
    [NotifyPropertyChangedFor(nameof(SelectedChangedFilePath))]
    [NotifyPropertyChangedFor(nameof(SelectedChangedFileHint))]
    [NotifyPropertyChangedFor(nameof(SelectedChangedFilePreview))]
    private SnapshotPendingFileItemViewModel? _selectedChangedFile;

    public ObservableCollection<SnapshotPendingFileItemViewModel> ChangedFiles { get; } = [];

    public SnapshotNameDialogWindowViewModel()
        : this($"snimok_{DateTime.Now:yyyyMMdd_HHmmss}")
    {
    }

    public SnapshotNameDialogWindowViewModel(string defaultName)
    {
        _snapshotName = string.IsNullOrWhiteSpace(defaultName)
            ? $"snimok_{DateTime.Now:yyyyMMdd_HHmmss}"
            : defaultName;
    }

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasChangedFiles => ChangedFiles.Count > 0;
    public bool HasNoChangedFiles => !HasChangedFiles;
    public bool HasSelectedChangedFile => SelectedChangedFile is not null;
    public bool CanSave => !string.IsNullOrWhiteSpace(SnapshotName) && HasChangedFiles;

    public string SelectedChangedFileTitle => SelectedChangedFile?.Name ?? "Select a changed file";

    public string SelectedChangedFilePath => SelectedChangedFile?.RelativePath
        ?? "No changed files detected for this snapshot.";

    public string SelectedChangedFileHint => SelectedChangedFile?.ComparisonHint
        ?? "When you select a file, you will see quick comparison metadata here.";

    public string SelectedChangedFilePreview => SelectedChangedFile?.DiffPreviewPlaceholder
        ?? "Diff preview placeholder: select a changed file to inspect.";

    public void Initialize(string defaultName, IReadOnlyCollection<SnapshotPendingFileItemViewModel> changedFiles)
    {
        SnapshotName = string.IsNullOrWhiteSpace(defaultName)
            ? $"snimok_{DateTime.Now:yyyyMMdd_HHmmss}"
            : defaultName;

        ChangedFiles.Clear();
        foreach (var file in changedFiles)
            ChangedFiles.Add(file);

        SelectedChangedFile = ChangedFiles.FirstOrDefault();

        OnPropertyChanged(nameof(HasChangedFiles));
        OnPropertyChanged(nameof(HasNoChangedFiles));
        OnPropertyChanged(nameof(CanSave));
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }

    [RelayCommand]
    private void Save()
    {
        if (!HasChangedFiles)
        {
            ErrorMessage = "No file changes detected. Snapshot creation is disabled.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SnapshotName))
        {
            ErrorMessage = "Snapshot name is required.";
            return;
        }

        var trimmed = SnapshotName.Trim();
        if (trimmed.Length > 256)
        {
            ErrorMessage = "Snapshot name is too long (max 256).";
            return;
        }

        SnapshotName = trimmed;
        ErrorMessage = null;
        RequestClose?.Invoke(true);
    }
}
