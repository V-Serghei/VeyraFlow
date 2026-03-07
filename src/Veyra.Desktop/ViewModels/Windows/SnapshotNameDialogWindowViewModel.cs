using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewColumns))]
    [NotifyPropertyChangedFor(nameof(HasNoPreviewColumns))]
    private string _previewLeftColumn = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewColumns))]
    [NotifyPropertyChangedFor(nameof(HasNoPreviewColumns))]
    private string _previewRightColumn = string.Empty;

    [ObservableProperty] private string _previewSummary = "Select a changed file to inspect the preview.";

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
    public bool HasPreviewColumns => !string.IsNullOrWhiteSpace(PreviewLeftColumn) || !string.IsNullOrWhiteSpace(PreviewRightColumn);
    public bool HasNoPreviewColumns => !HasPreviewColumns;

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
        PreviewLeftColumn = string.Empty;
        PreviewRightColumn = string.Empty;

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

            BuildSideBySideColumns(preview.Lines, preview.Hunks, out var left, out var right);
            PreviewLeftColumn = left;
            PreviewRightColumn = right;
            PreviewSummary = string.IsNullOrWhiteSpace(preview.Message)
                ? $"+{preview.AddedLines} / -{preview.RemovedLines}" + (preview.IsTruncated ? " (truncated)" : string.Empty)
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
        PreviewLeftColumn = string.Empty;
        PreviewRightColumn = string.Empty;
    }

    private static void BuildSideBySideColumns(
        IReadOnlyList<TextDiffLineDto> lines,
        IReadOnlyList<TextDiffHunkDto> hunks,
        out string left,
        out string right)
    {
        var leftBuilder = new StringBuilder();
        var rightBuilder = new StringBuilder();

        if (lines.Count == 0)
        {
            left = string.Empty;
            right = string.Empty;
            return;
        }

        if (hunks.Count > 0)
        {
            var firstHunk = true;
            foreach (var hunk in hunks.OrderBy(h => h.Sequence))
            {
                var start = Math.Clamp(hunk.StartLineSequence, 0, lines.Count - 1);
                var end = Math.Clamp(hunk.EndLineSequence, start, lines.Count - 1);

                if (!firstHunk)
                {
                    AppendDiffColumnLine(leftBuilder, null, '~', "...");
                    AppendDiffColumnLine(rightBuilder, null, '~', "...");
                }

                for (var i = start; i <= end; i++)
                    AppendPreviewLine(lines[i], leftBuilder, rightBuilder);

                firstHunk = false;
            }
        }
        else
        {
            foreach (var line in lines)
                AppendPreviewLine(line, leftBuilder, rightBuilder);
        }

        left = leftBuilder.ToString().TrimEnd();
        right = rightBuilder.ToString().TrimEnd();
    }

    private static void AppendPreviewLine(
        TextDiffLineDto line,
        StringBuilder leftBuilder,
        StringBuilder rightBuilder)
    {
        switch (line.Kind)
        {
            case "remove":
                AppendDiffColumnLine(leftBuilder, line.LeftLineNumber, '-', line.Text);
                AppendDiffColumnLine(rightBuilder, null, ' ', string.Empty);
                break;
            case "add":
                AppendDiffColumnLine(leftBuilder, null, ' ', string.Empty);
                AppendDiffColumnLine(rightBuilder, line.RightLineNumber, '+', line.Text);
                break;
            default:
                AppendDiffColumnLine(leftBuilder, line.LeftLineNumber, ' ', line.Text);
                AppendDiffColumnLine(rightBuilder, line.RightLineNumber, ' ', line.Text);
                break;
        }
    }

    private static void AppendDiffColumnLine(StringBuilder builder, int? lineNumber, char marker, string text)
    {
        if (lineNumber is null)
            builder.Append("    ");
        else
            builder.Append(lineNumber.Value.ToString("D4"));

        builder.Append(' ');
        builder.Append(marker);
        builder.Append(' ');
        builder.AppendLine(text ?? string.Empty);
    }
}
