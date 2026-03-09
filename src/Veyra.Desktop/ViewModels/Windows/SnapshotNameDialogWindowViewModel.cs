using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Application.DTOs;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class SnapshotNameDialogWindowViewModel : ObservableObject
{
    public event Action<bool>? RequestClose;

    private Func<SnapshotPendingFileItemViewModel, CancellationToken, Task<PendingFileDiffPreviewDto>>? _previewLoader;
    private CancellationTokenSource? _previewCts;
    private readonly List<string> _tempPreviewFiles = [];

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoPreviewContent))]
    private bool _isPreviewLoading;

    [ObservableProperty]
    private string _previewSummary = "Select a changed file to inspect the preview.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextPreview))]
    [NotifyPropertyChangedFor(nameof(IsBinaryPreview))]
    [NotifyPropertyChangedFor(nameof(IsImagePreview))]
    [NotifyPropertyChangedFor(nameof(HasNoPreviewContent))]
    private PendingDiffPreviewKind _previewKind = PendingDiffPreviewKind.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImagePreviews))]
    [NotifyPropertyChangedFor(nameof(HasNoImagePreviews))]
    private Bitmap? _leftImagePreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImagePreviews))]
    [NotifyPropertyChangedFor(nameof(HasNoImagePreviews))]
    private Bitmap? _rightImagePreview;

    [ObservableProperty] private string _leftImageCaption = "Before";
    [ObservableProperty] private string _rightImageCaption = "After";

    public ObservableCollection<SnapshotPendingFileItemViewModel> ChangedFiles { get; } = [];
    public ObservableCollection<SnapshotDiffRowItemViewModel> PreviewRows { get; } = [];
    public ObservableCollection<SnapshotPreviewMetricItemViewModel> PreviewMetrics { get; } = [];

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
        PreviewMetrics.CollectionChanged += OnPreviewMetricsCollectionChanged;
    }

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasChangedFiles => ChangedFiles.Count > 0;
    public bool HasNoChangedFiles => !HasChangedFiles;
    public bool HasSelectedChangedFile => SelectedChangedFile is not null;
    public bool CanSave => !string.IsNullOrWhiteSpace(SnapshotName) && HasChangedFiles;

    public bool HasPreviewRows => PreviewRows.Count > 0;
    public bool HasPreviewMetrics => PreviewMetrics.Count > 0;
    public bool HasImagePreviews => LeftImagePreview is not null || RightImagePreview is not null;
    public bool HasNoImagePreviews => !HasImagePreviews;

    public bool IsTextPreview => PreviewKind == PendingDiffPreviewKind.Text && HasPreviewRows;
    public bool IsBinaryPreview => PreviewKind == PendingDiffPreviewKind.Binary && HasPreviewMetrics;
    public bool IsImagePreview => PreviewKind == PendingDiffPreviewKind.Image;

    public bool HasNoPreviewContent => !IsPreviewLoading && !IsTextPreview && !IsBinaryPreview && !IsImagePreview;

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

    public void CleanupPreviewResources()
    {
        _previewCts?.Cancel();
        ReleasePreviewResources();
    }

    partial void OnSelectedChangedFileChanged(SnapshotPendingFileItemViewModel? value)
    {
        _ = LoadPreviewForSelectionAsync(value);
    }

    [RelayCommand]
    private void Cancel()
    {
        _previewCts?.Cancel();
        ReleasePreviewResources();
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
        ReleasePreviewResources();
        RequestClose?.Invoke(true);
    }

    private async Task LoadPreviewForSelectionAsync(SnapshotPendingFileItemViewModel? file)
    {
        _previewCts?.Cancel();
        ReleasePreviewResources();

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
        PreviewKind = PendingDiffPreviewKind.None;

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

            switch (preview.Kind)
            {
                case PendingDiffPreviewKind.Text:
                {
                    var rows = BuildPreviewRows(preview.Lines, preview.Hunks);
                    PreviewRows.Clear();
                    foreach (var row in rows)
                        PreviewRows.Add(row);

                    PreviewKind = PendingDiffPreviewKind.Text;
                    PreviewSummary = string.IsNullOrWhiteSpace(preview.Message)
                        ? $"{preview.AddedLines} added / {preview.RemovedLines} removed"
                            + (preview.IsTruncated ? " (preview truncated)" : string.Empty)
                        : preview.Message;
                    break;
                }
                case PendingDiffPreviewKind.Binary:
                {
                    ApplyBinaryMetrics(preview.BinarySummary, null);
                    PreviewKind = PendingDiffPreviewKind.Binary;
                    PreviewSummary = string.IsNullOrWhiteSpace(preview.Message)
                        ? "Binary summary is ready."
                        : preview.Message;
                    break;
                }
                case PendingDiffPreviewKind.Image:
                {
                    await LoadImagePreviewAsync(preview.ImagePreview, cts.Token);
                    ApplyBinaryMetrics(preview.BinarySummary, preview.ImagePreview);
                    PreviewKind = PendingDiffPreviewKind.Image;
                    PreviewSummary = string.IsNullOrWhiteSpace(preview.Message)
                        ? "Image comparison is ready."
                        : preview.Message;
                    break;
                }
                default:
                    ResetPreview("Preview format is not supported.");
                    break;
            }
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

    private async Task LoadImagePreviewAsync(PendingImageDiffPreviewDto? imagePreview, CancellationToken ct)
    {
        LeftImageCaption = "Before";
        RightImageCaption = "After";

        if (imagePreview is null)
            return;

        if (!string.IsNullOrWhiteSpace(imagePreview.BaselineImagePath)
            && File.Exists(imagePreview.BaselineImagePath))
        {
            LeftImagePreview = await Task.Run(() => new Bitmap(imagePreview.BaselineImagePath), ct);
            if (imagePreview.IsBaselineTempFile)
                _tempPreviewFiles.Add(imagePreview.BaselineImagePath);
        }

        if (!string.IsNullOrWhiteSpace(imagePreview.CurrentImagePath)
            && File.Exists(imagePreview.CurrentImagePath))
        {
            RightImagePreview = await Task.Run(() => new Bitmap(imagePreview.CurrentImagePath), ct);
            if (imagePreview.IsCurrentTempFile)
                _tempPreviewFiles.Add(imagePreview.CurrentImagePath);
        }

        LeftImageCaption = BuildImageSideCaption("Before", imagePreview.BaselineWidth, imagePreview.BaselineHeight);
        RightImageCaption = BuildImageSideCaption("After", imagePreview.CurrentWidth, imagePreview.CurrentHeight);
    }

    private static string BuildImageSideCaption(string prefix, int? width, int? height)
    {
        if (width is null || height is null)
            return prefix;

        return $"{prefix} · {width} x {height}";
    }

    private void ApplyBinaryMetrics(
        PendingBinaryDiffSummaryDto? summary,
        PendingImageDiffPreviewDto? imagePreview)
    {
        PreviewMetrics.Clear();
        if (summary is null)
            return;

        PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
            "Size",
            $"{FormatBytes(summary.BaselineSizeBytes)} -> {FormatBytes(summary.CurrentSizeBytes)} ({FormatSignedBytes(summary.SizeDeltaBytes)})"));

        PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
            "SHA-256",
            $"before {ShortHash(summary.BaselineHashSha256)} | after {ShortHash(summary.CurrentHashSha256)}"));

        PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
            "Blocks",
            $"{summary.BaselineBlockCount} -> {summary.CurrentBlockCount}, shared {summary.SharedBlockCount}"));

        PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
            "Dedup / Changed",
            $"{FormatRatio(summary.DedupRatio)} / {FormatRatio(summary.ChangedBlockRatio)}"));

        if (summary.ByteSimilarityRatio.HasValue)
        {
            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                "Byte similarity",
                $"{summary.ByteSimilarityRatio.Value * 100:F1}%"));
        }

        if (imagePreview is not null)
        {
            var before = imagePreview.BaselineWidth is null || imagePreview.BaselineHeight is null
                ? "n/a"
                : $"{imagePreview.BaselineWidth} x {imagePreview.BaselineHeight}";

            var after = imagePreview.CurrentWidth is null || imagePreview.CurrentHeight is null
                ? "n/a"
                : $"{imagePreview.CurrentWidth} x {imagePreview.CurrentHeight}";

            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                "Dimensions",
                $"{before} -> {after}" + (imagePreview.HasDimensionMismatch ? " (changed)" : "")));

            if (imagePreview.SimilarityRatio.HasValue)
            {
                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    "Image similarity",
                    $"{imagePreview.SimilarityRatio.Value * 100:F1}% (byte-level)"));
            }
        }
    }

    private static string FormatRatio(double? value)
        => value.HasValue ? $"{value.Value * 100:F1}%" : "n/a";

    private static string ShortHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "n/a";

        return value.Length <= 16 ? value : $"{value[..8]}...{value[^8..]}";
    }

    private static string FormatSignedBytes(long value)
    {
        if (value == 0)
            return "0 B";

        var sign = value > 0 ? "+" : "-";
        var abs = value == long.MinValue ? long.MaxValue : Math.Abs(value);
        return $"{sign}{FormatBytes(abs)}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private void ResetPreview(string message)
    {
        PreviewKind = PendingDiffPreviewKind.None;
        IsPreviewLoading = false;
        PreviewSummary = message;
        PreviewRows.Clear();
        PreviewMetrics.Clear();
    }

    private void ReleasePreviewResources()
    {
        PreviewRows.Clear();
        PreviewMetrics.Clear();
        PreviewKind = PendingDiffPreviewKind.None;

        LeftImagePreview?.Dispose();
        RightImagePreview?.Dispose();
        LeftImagePreview = null;
        RightImagePreview = null;

        LeftImageCaption = "Before";
        RightImageCaption = "After";

        foreach (var tempFile in _tempPreviewFiles)
        {
            try
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
            catch
            {
            }
        }

        _tempPreviewFiles.Clear();
    }

    private static IReadOnlyList<SnapshotDiffRowItemViewModel> BuildPreviewRows(
        IReadOnlyList<TextDiffLineDto> lines,
        IReadOnlyList<TextDiffHunkDto> hunks)
    {
        if (lines.Count == 0)
            return [];

        var rows = new List<SnapshotDiffRowItemViewModel>(lines.Count + (hunks.Count * 3));

        if (hunks.Count > 0)
        {
            foreach (var hunk in hunks.OrderBy(h => h.Sequence))
            {
                var start = Math.Clamp(hunk.StartLineSequence, 0, lines.Count - 1);
                var end = Math.Clamp(hunk.EndLineSequence, start, lines.Count - 1);

                rows.Add(SnapshotDiffRowItemViewModel.CreateHunkHeader(
                    FormatHunkRange(hunk.OldStartLine, hunk.OldLineCount),
                    FormatHunkRange(hunk.NewStartLine, hunk.NewLineCount),
                    NormalizeChangeKindLabel(hunk.ChangeKind)));

                AppendHunkRows(lines, start, end, rows);
            }
        }
        else
        {
            rows.Add(SnapshotDiffRowItemViewModel.CreateHunkHeader("(full)", "(full)", "context"));
            AppendHunkRows(lines, 0, lines.Count - 1, rows);
        }

        return rows;
    }

    private static void AppendHunkRows(
        IReadOnlyList<TextDiffLineDto> lines,
        int startInclusive,
        int endInclusive,
        ICollection<SnapshotDiffRowItemViewModel> rows)
    {
        if (startInclusive > endInclusive)
            return;

        var index = startInclusive;
        while (index <= endInclusive)
        {
            var kind = NormalizeDiffKind(lines[index].Kind);

            if (kind == "remove")
            {
                var removed = new List<TextDiffLineDto>();
                while (index <= endInclusive && NormalizeDiffKind(lines[index].Kind) == "remove")
                {
                    removed.Add(lines[index]);
                    index++;
                }

                var added = new List<TextDiffLineDto>();
                var addCursor = index;
                while (addCursor <= endInclusive && NormalizeDiffKind(lines[addCursor].Kind) == "add")
                {
                    added.Add(lines[addCursor]);
                    addCursor++;
                }

                if (added.Count > 0)
                    index = addCursor;

                var pairCount = Math.Max(removed.Count, added.Count);
                for (var i = 0; i < pairCount; i++)
                {
                    var left = i < removed.Count ? removed[i] : null;
                    var right = i < added.Count ? added[i] : null;
                    rows.Add(CreatePairedDiffRow(left, right));
                }

                continue;
            }

            if (kind == "add")
            {
                while (index <= endInclusive && NormalizeDiffKind(lines[index].Kind) == "add")
                {
                    rows.Add(CreatePairedDiffRow(null, lines[index]));
                    index++;
                }

                continue;
            }

            rows.Add(CreatePairedDiffRow(lines[index], lines[index]));
            index++;
        }
    }

    private static SnapshotDiffRowItemViewModel CreatePairedDiffRow(
        TextDiffLineDto? left,
        TextDiffLineDto? right)
    {
        var leftKind = NormalizeDiffKind(left?.Kind);
        var rightKind = NormalizeDiffKind(right?.Kind);

        return new SnapshotDiffRowItemViewModel
        {
            LeftLineNumber = left is null ? string.Empty : FormatLineNumber(left.LeftLineNumber),
            LeftMarker = leftKind switch
            {
                "remove" => "-",
                "equal" => "|",
                _ => " "
            },
            LeftText = left?.Text ?? string.Empty,
            LeftBackground = leftKind switch
            {
                "remove" => "#45202B",
                "equal" => "#173149",
                _ => "#10233A"
            },
            LeftMarkerForeground = leftKind switch
            {
                "remove" => "#FF8FA3",
                "equal" => "#9BB5D1",
                _ => "#94AECB"
            },
            RightLineNumber = right is null ? string.Empty : FormatLineNumber(right.RightLineNumber),
            RightMarker = rightKind switch
            {
                "add" => "+",
                "equal" => "|",
                _ => " "
            },
            RightText = right?.Text ?? string.Empty,
            RightBackground = rightKind switch
            {
                "add" => "#1E4A39",
                "equal" => "#173149",
                _ => "#10233A"
            },
            RightMarkerForeground = rightKind switch
            {
                "add" => "#89FFD0",
                "equal" => "#9BB5D1",
                _ => "#94AECB"
            }
        };
    }

    private static string NormalizeDiffKind(string? kind)
    {
        if (string.Equals(kind, "remove", StringComparison.OrdinalIgnoreCase))
            return "remove";

        if (string.Equals(kind, "add", StringComparison.OrdinalIgnoreCase))
            return "add";

        return "equal";
    }

    private static string NormalizeChangeKindLabel(string? kind)
    {
        if (string.Equals(kind, "added", StringComparison.OrdinalIgnoreCase))
            return "added";

        if (string.Equals(kind, "removed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "deleted", StringComparison.OrdinalIgnoreCase))
            return "removed";

        return "modified";
    }

    private static string FormatHunkRange(int startLine, int count)
        => count <= 0
            ? $"{Math.Max(0, startLine)}"
            : $"{Math.Max(0, startLine)},{count}";

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
        OnPropertyChanged(nameof(IsTextPreview));
        OnPropertyChanged(nameof(HasNoPreviewContent));
    }

    private void OnPreviewMetricsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasPreviewMetrics));
        OnPropertyChanged(nameof(IsBinaryPreview));
        OnPropertyChanged(nameof(HasNoPreviewContent));
    }
}


