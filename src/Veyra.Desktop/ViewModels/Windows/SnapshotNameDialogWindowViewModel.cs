using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Application.DTOs;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class SnapshotNameDialogWindowViewModel : ObservableObject
{
    public event Action<bool>? RequestClose;

    private Func<SnapshotPendingFileItemViewModel, CancellationToken, Task<PendingFileDiffPreviewDto>>? _previewLoader;
    private CancellationTokenSource? _previewCts;

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
    private SnapshotPendingFileItemViewModel? _selectedChangedFile;

    [ObservableProperty] private bool _isPreviewLoading;

    [ObservableProperty] private string _previewSummary = "Select a changed file to inspect the preview.";

    public ObservableCollection<SnapshotPendingFileItemViewModel> ChangedFiles { get; } = [];
    public ObservableCollection<SnapshotDiffRowItemViewModel> PreviewRows { get; } = [];

    public SnapshotNameDialogWindowViewModel()
        : this($"snimok_{DateTime.Now:yyyyMMdd_HHmmss}")
    {
    }

    public SnapshotNameDialogWindowViewModel(string defaultName)
    {
        _snapshotName = string.IsNullOrWhiteSpace(defaultName)
            ? $"snimok_{DateTime.Now:yyyyMMdd_HHmmss}"
            : defaultName;

        ChangedFiles.CollectionChanged += OnChangedFilesCollectionChanged;
        PreviewRows.CollectionChanged += OnPreviewRowsCollectionChanged;
    }

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasChangedFiles => ChangedFiles.Count > 0;
    public bool HasNoChangedFiles => !HasChangedFiles;
    public bool HasSelectedChangedFile => SelectedChangedFile is not null;
    public bool CanSave => !string.IsNullOrWhiteSpace(SnapshotName) && HasChangedFiles;
    public bool HasPreviewRows => PreviewRows.Count > 0;
    public bool HasNoPreviewRows => !HasPreviewRows;
    public string ChangedFilesCountLabel => HasChangedFiles
        ? $"{ChangedFiles.Count} changed file{(ChangedFiles.Count == 1 ? string.Empty : "s") }"
        : "No changed files detected";

    public string SelectedChangedFileTitle => SelectedChangedFile?.Name ?? "Select a changed file";

    public string SelectedChangedFilePath => SelectedChangedFile?.RelativePath
        ?? "No changed files detected for this snapshot.";

    public string SelectedChangedFileHint => SelectedChangedFile?.ComparisonHint
        ?? "When you select a file, you will see quick comparison metadata here.";

    public void Initialize(
        string defaultName,
        IReadOnlyCollection<SnapshotPendingFileItemViewModel> changedFiles,
        Func<SnapshotPendingFileItemViewModel, CancellationToken, Task<PendingFileDiffPreviewDto>>? previewLoader = null)
    {
        SnapshotName = string.IsNullOrWhiteSpace(defaultName)
            ? $"snimok_{DateTime.Now:yyyyMMdd_HHmmss}"
            : defaultName;

        _previewLoader = previewLoader;

        ChangedFiles.Clear();
        foreach (var file in changedFiles)
            ChangedFiles.Add(file);

        SelectedChangedFile = ChangedFiles.FirstOrDefault();

        if (SelectedChangedFile is null)
            ResetPreview("No changed files detected for this snapshot.");

        OnPropertyChanged(nameof(HasChangedFiles));
        OnPropertyChanged(nameof(HasNoChangedFiles));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ChangedFilesCountLabel));
    }

    partial void OnSelectedChangedFileChanged(SnapshotPendingFileItemViewModel? value)
    {
        _ = LoadPreviewForSelectionAsync(value);
    }

    [RelayCommand]
    private void Cancel()
    {
        _previewCts?.Cancel();
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

    private async Task LoadPreviewForSelectionAsync(SnapshotPendingFileItemViewModel? file)
    {
        _previewCts?.Cancel();

        if (file is null)
        {
            ResetPreview("Select a changed file to inspect the preview.");
            return;
        }

        if (!string.Equals(file.ChangeKind, "modified", StringComparison.OrdinalIgnoreCase))
        {
            ResetPreview(file.DiffPreviewPlaceholder);
            return;
        }

        if (_previewLoader is null)
        {
            ResetPreview(file.DiffPreviewPlaceholder);
            return;
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;

        IsPreviewLoading = true;
        PreviewSummary = "Building preview...";
        PreviewRows.Clear();

        try
        {
            var preview = await _previewLoader(file, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            if (!preview.IsAvailable)
            {
                ResetPreview(preview.Message);
                return;
            }

            var rows = BuildPreviewRows(preview.Lines, preview.Hunks);
            PreviewRows.Clear();
            foreach (var row in rows)
                PreviewRows.Add(row);

            PreviewSummary = string.IsNullOrWhiteSpace(preview.Message)
                ? $"{preview.AddedLines} added / {preview.RemovedLines} removed"
                    + (preview.IsTruncated ? " (preview truncated)" : string.Empty)
                : preview.Message;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            ResetPreview("Unable to load diff preview for selected file.");
        }
        finally
        {
            if (!cts.IsCancellationRequested)
                IsPreviewLoading = false;
        }
    }

    private void ResetPreview(string message)
    {
        IsPreviewLoading = false;
        PreviewSummary = message;
        PreviewRows.Clear();
    }

    private static IReadOnlyList<SnapshotDiffRowItemViewModel> BuildPreviewRows(
        IReadOnlyList<TextDiffLineDto> lines,
        IReadOnlyList<TextDiffHunkDto> hunks)
    {
        if (lines.Count == 0)
            return [];

        var rows = new List<SnapshotDiffRowItemViewModel>(lines.Count + 8);

        if (hunks.Count > 0)
        {
            var firstHunk = true;
            foreach (var hunk in hunks.OrderBy(h => h.Sequence))
            {
                var start = Math.Clamp(hunk.StartLineSequence, 0, lines.Count - 1);
                var end = Math.Clamp(hunk.EndLineSequence, start, lines.Count - 1);

                if (!firstHunk)
                    rows.Add(SnapshotDiffRowItemViewModel.CreateSeparator());

                for (var i = start; i <= end; i++)
                    rows.Add(CreateDiffRow(lines[i]));

                firstHunk = false;
            }
        }
        else
        {
            foreach (var line in lines)
                rows.Add(CreateDiffRow(line));
        }

        return rows;
    }

    private static SnapshotDiffRowItemViewModel CreateDiffRow(TextDiffLineDto line)
    {
        var normalizedKind = (line.Kind ?? string.Empty).Trim().ToLowerInvariant();

        return normalizedKind switch
        {
            "remove" => new SnapshotDiffRowItemViewModel
            {
                LeftLineNumber = FormatLineNumber(line.LeftLineNumber),
                LeftMarker = "-",
                LeftText = line.Text ?? string.Empty,
                LeftBackground = "#332028",
                LeftMarkerForeground = "#FF9AA5",
                RightLineNumber = string.Empty,
                RightMarker = " ",
                RightText = string.Empty,
                RightBackground = "#17263A",
                RightMarkerForeground = "#8FA5BF"
            },
            "add" => new SnapshotDiffRowItemViewModel
            {
                LeftLineNumber = string.Empty,
                LeftMarker = " ",
                LeftText = string.Empty,
                LeftBackground = "#17263A",
                LeftMarkerForeground = "#8FA5BF",
                RightLineNumber = FormatLineNumber(line.RightLineNumber),
                RightMarker = "+",
                RightText = line.Text ?? string.Empty,
                RightBackground = "#1E3A31",
                RightMarkerForeground = "#8AF5C5"
            },
            _ => new SnapshotDiffRowItemViewModel
            {
                LeftLineNumber = FormatLineNumber(line.LeftLineNumber),
                LeftMarker = " ",
                LeftText = line.Text ?? string.Empty,
                LeftBackground = "#1B2C42",
                LeftMarkerForeground = "#8FA5BF",
                RightLineNumber = FormatLineNumber(line.RightLineNumber),
                RightMarker = " ",
                RightText = line.Text ?? string.Empty,
                RightBackground = "#1B2C42",
                RightMarkerForeground = "#8FA5BF"
            }
        };
    }

    private static string FormatLineNumber(int? lineNumber)
        => lineNumber is int value ? value.ToString("D4") : string.Empty;

    private void OnChangedFilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasChangedFiles));
        OnPropertyChanged(nameof(HasNoChangedFiles));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ChangedFilesCountLabel));
    }

    private void OnPreviewRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasPreviewRows));
        OnPropertyChanged(nameof(HasNoPreviewRows));
    }
}