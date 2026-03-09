using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Application.DTOs;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class SnapshotNameDialogWindowViewModel : ObservableObject
{
    private const int ContextCollapseThreshold = 14;
    private const int ContextKeepEdgeLines = 3;
    private static readonly Regex WordDiffTokenRegex = new(@"\w+|\s+|[^\w\s]", RegexOptions.Compiled);
    public event Action<bool>? RequestClose;

    private Func<SnapshotPendingFileItemViewModel, CancellationToken, Task<PendingFileDiffPreviewDto>>? _previewLoader;
    private CancellationTokenSource? _previewCts;
    private readonly List<string> _tempPreviewFiles = [];

    private IReadOnlyList<TextDiffLineDto> _currentTextLines = Array.Empty<TextDiffLineDto>();
    private IReadOnlyList<TextDiffHunkDto> _currentTextHunks = Array.Empty<TextDiffHunkDto>();

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WrapToggleLabel))]
    private bool _isWrapEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPinnedHunkHeader))]
    private string _pinnedHunkHeader = string.Empty;

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
    public bool HasPinnedHunkHeader => !string.IsNullOrWhiteSpace(PinnedHunkHeader);

    public bool IsTextPreview => PreviewKind == PendingDiffPreviewKind.Text && HasPreviewRows;
    public bool IsBinaryPreview => PreviewKind == PendingDiffPreviewKind.Binary && HasPreviewMetrics;
    public bool IsImagePreview => PreviewKind == PendingDiffPreviewKind.Image;

    public bool HasNoPreviewContent => !IsPreviewLoading && !IsTextPreview && !IsBinaryPreview && !IsImagePreview;

    public string WrapToggleLabel => IsWrapEnabled ? "Wrap: On" : "Wrap: Off";

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

    [RelayCommand]
    private void ToggleWrapMode()
    {
        IsWrapEnabled = !IsWrapEnabled;
    }

    [RelayCommand]
    private void ExpandContextFold(SnapshotDiffRowItemViewModel? row)
    {
        if (row is null || !row.IsContextFoldRow || row.HiddenContextRows.Count == 0)
            return;

        var index = PreviewRows.IndexOf(row);
        if (index < 0)
            return;

        PreviewRows.RemoveAt(index);

        var insertAt = index;
        foreach (var hidden in row.HiddenContextRows)
        {
            PreviewRows.Insert(insertAt, hidden);
            insertAt++;
        }

        RefreshPinnedHunkHeaderFromRows();
    }

    [RelayCommand]
    private void PinHunkHeader(SnapshotDiffRowItemViewModel? row)
    {
        if (row is null)
            return;

        if (row.IsHunkHeader)
        {
            PinnedHunkHeader = row.HunkHeader;
            return;
        }

        var index = PreviewRows.IndexOf(row);
        if (index < 0)
            return;

        for (var i = index; i >= 0; i--)
        {
            if (!PreviewRows[i].IsHunkHeader)
                continue;

            PinnedHunkHeader = PreviewRows[i].HunkHeader;
            return;
        }
    }

    public void UpdatePinnedHunkHeaderByScroll(double verticalOffset)
    {
        if (PreviewRows.Count == 0)
        {
            PinnedHunkHeader = string.Empty;
            return;
        }

        const double estimatedRowHeight = 34d;
        var startIndex = (int)Math.Floor(Math.Max(0, verticalOffset) / estimatedRowHeight);
        if (startIndex >= PreviewRows.Count)
            startIndex = PreviewRows.Count - 1;

        for (var i = startIndex; i >= 0; i--)
        {
            if (!PreviewRows[i].IsHunkHeader)
                continue;

            PinnedHunkHeader = PreviewRows[i].HunkHeader;
            return;
        }

        PinnedHunkHeader = PreviewRows.FirstOrDefault(x => x.IsHunkHeader)?.HunkHeader ?? string.Empty;
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
                    _currentTextLines = preview.Lines;
                    _currentTextHunks = preview.Hunks;
                    RebuildTextPreviewRows();

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

    private void RebuildTextPreviewRows()
    {
        PreviewRows.Clear();

        if (_currentTextLines.Count == 0)
        {
            PinnedHunkHeader = string.Empty;
            return;
        }

        var rows = BuildPreviewRows(_currentTextLines, _currentTextHunks);
        foreach (var row in rows)
            PreviewRows.Add(row);

        RefreshPinnedHunkHeaderFromRows();
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

        return $"{prefix} - {width} x {height}";
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
                $"{before} -> {after}" + (imagePreview.HasDimensionMismatch ? " (changed)" : string.Empty)));

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
        PinnedHunkHeader = string.Empty;
        _currentTextLines = Array.Empty<TextDiffLineDto>();
        _currentTextHunks = Array.Empty<TextDiffHunkDto>();
    }

    private void ReleasePreviewResources()
    {
        PreviewRows.Clear();
        PreviewMetrics.Clear();
        PreviewKind = PendingDiffPreviewKind.None;
        PinnedHunkHeader = string.Empty;
        _currentTextLines = Array.Empty<TextDiffLineDto>();
        _currentTextHunks = Array.Empty<TextDiffHunkDto>();

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
            var hunkSequence = 0;
            foreach (var hunk in hunks.OrderBy(h => h.Sequence))
            {
                hunkSequence++;

                var start = Math.Clamp(hunk.StartLineSequence, 0, lines.Count - 1);
                var end = Math.Clamp(hunk.EndLineSequence, start, lines.Count - 1);

                rows.Add(SnapshotDiffRowItemViewModel.CreateHunkHeader(
                    hunkSequence,
                    FormatHunkRange(hunk.OldStartLine, hunk.OldLineCount),
                    FormatHunkRange(hunk.NewStartLine, hunk.NewLineCount),
                    NormalizeChangeKindLabel(hunk.ChangeKind)));

                var hunkRows = new List<SnapshotDiffRowItemViewModel>();
                AppendHunkRows(lines, start, end, hunkSequence, hunkRows);
                rows.AddRange(CollapseContextRows(hunkRows, hunkSequence));
            }
        }
        else
        {
            rows.Add(SnapshotDiffRowItemViewModel.CreateHunkHeader(1, "(full)", "(full)", "context"));
            var hunkRows = new List<SnapshotDiffRowItemViewModel>();
            AppendHunkRows(lines, 0, lines.Count - 1, 1, hunkRows);
            rows.AddRange(CollapseContextRows(hunkRows, 1));
        }

        return rows;
    }

    private static IReadOnlyList<SnapshotDiffRowItemViewModel> CollapseContextRows(
        IReadOnlyList<SnapshotDiffRowItemViewModel> source,
        int hunkSequence)
    {
        if (source.Count == 0)
            return source;

        var collapsed = new List<SnapshotDiffRowItemViewModel>(source.Count);

        var index = 0;
        while (index < source.Count)
        {
            if (!string.Equals(source[index].DiffKind, "context", StringComparison.Ordinal))
            {
                collapsed.Add(source[index]);
                index++;
                continue;
            }

            var start = index;
            while (index < source.Count
                   && string.Equals(source[index].DiffKind, "context", StringComparison.Ordinal))
            {
                index++;
            }

            var count = index - start;
            if (count <= ContextCollapseThreshold)
            {
                for (var i = start; i < index; i++)
                    collapsed.Add(source[i]);

                continue;
            }

            var keepHead = Math.Min(ContextKeepEdgeLines, count / 2);
            var keepTail = Math.Min(ContextKeepEdgeLines, count - keepHead);
            var hiddenCount = count - keepHead - keepTail;

            for (var i = start; i < start + keepHead; i++)
                collapsed.Add(source[i]);

            var hidden = source.Skip(start + keepHead)
                .Take(hiddenCount)
                .ToList();

            collapsed.Add(SnapshotDiffRowItemViewModel.CreateContextFold(
                hunkSequence,
                hiddenCount,
                hidden));

            for (var i = index - keepTail; i < index; i++)
                collapsed.Add(source[i]);
        }

        return collapsed;
    }

    private static void AppendHunkRows(
        IReadOnlyList<TextDiffLineDto> lines,
        int startInclusive,
        int endInclusive,
        int hunkSequence,
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
                    rows.Add(CreatePairedDiffRow(left, right, hunkSequence));
                }

                continue;
            }

            if (kind == "add")
            {
                while (index <= endInclusive && NormalizeDiffKind(lines[index].Kind) == "add")
                {
                    rows.Add(CreatePairedDiffRow(null, lines[index], hunkSequence));
                    index++;
                }

                continue;
            }

            rows.Add(CreatePairedDiffRow(lines[index], lines[index], hunkSequence));
            index++;
        }
    }

    private static SnapshotDiffRowItemViewModel CreatePairedDiffRow(
        TextDiffLineDto? left,
        TextDiffLineDto? right,
        int hunkSequence)
    {
        var leftKind = NormalizeDiffKind(left?.Kind);
        var rightKind = NormalizeDiffKind(right?.Kind);

        var rowKind = ResolveRowKind(leftKind, rightKind, left, right);

        var leftSegments = BuildSideSegments(
            left?.Text ?? string.Empty,
            rowKind,
            isRightSide: false,
            left?.Text,
            right?.Text);

        var rightSegments = BuildSideSegments(
            right?.Text ?? string.Empty,
            rowKind,
            isRightSide: true,
            left?.Text,
            right?.Text);

        return SnapshotDiffRowItemViewModel.CreateContentRow(
            hunkSequence,
            rowKind,
            left is null ? string.Empty : FormatLineNumber(left.LeftLineNumber),
            GetMarker(rowKind, isRightSide: false),
            GetBackground(rowKind, isRightSide: false),
            GetMarkerForeground(rowKind, isRightSide: false),
            leftSegments,
            right is null ? string.Empty : FormatLineNumber(right.RightLineNumber),
            GetMarker(rowKind, isRightSide: true),
            GetBackground(rowKind, isRightSide: true),
            GetMarkerForeground(rowKind, isRightSide: true),
            rightSegments);
    }

    private static IReadOnlyList<SnapshotDiffTextSegmentViewModel> BuildSideSegments(
        string sideText,
        string rowKind,
        bool isRightSide,
        string? leftText,
        string? rightText)
    {
        if (rowKind == "modified"
            && !string.IsNullOrEmpty(leftText)
            && !string.IsNullOrEmpty(rightText))
        {
            return BuildModifiedSegments(leftText, rightText, isRightSide);
        }

        var (fg, bg, emphasized) = GetSegmentColors(rowKind, isRightSide, isChangedChunk: true);
        return
        [
            new SnapshotDiffTextSegmentViewModel
            {
                Text = sideText,
                Foreground = fg,
                Background = bg,
                IsEmphasized = emphasized
            }
        ];
    }

    private static IReadOnlyList<SnapshotDiffTextSegmentViewModel> BuildModifiedSegments(
        string leftText,
        string rightText,
        bool isRightSide)
    {
        if (string.Equals(leftText, rightText, StringComparison.Ordinal))
            return BuildSideSegments(isRightSide ? rightText : leftText, "context", isRightSide, leftText, rightText);

        var leftTokens = TokenizeWordDiffText(leftText);
        var rightTokens = TokenizeWordDiffText(rightText);
        var operations = BuildWordDiffOperations(leftTokens, rightTokens);

        var segments = new List<SnapshotDiffTextSegmentViewModel>(Math.Max(leftTokens.Count, rightTokens.Count) + 2);

        foreach (var operation in operations)
        {
            switch (operation.Kind)
            {
                case WordDiffOperationKind.Equal:
                    AppendSegment(segments, operation.Token, "context", isRightSide, isChangedChunk: false);
                    break;
                case WordDiffOperationKind.Remove when !isRightSide:
                    AppendSegment(segments, operation.Token, "remove", isRightSide, isChangedChunk: true);
                    break;
                case WordDiffOperationKind.Add when isRightSide:
                    AppendSegment(segments, operation.Token, "add", isRightSide, isChangedChunk: true);
                    break;
            }
        }

        if (segments.Count == 0)
        {
            var (fg, bg, emphasized) = GetSegmentColors("context", isRightSide, isChangedChunk: false);
            segments.Add(new SnapshotDiffTextSegmentViewModel
            {
                Text = string.Empty,
                Foreground = fg,
                Background = bg,
                IsEmphasized = emphasized
            });
        }

        return segments;
    }

    private static IReadOnlyList<string> TokenizeWordDiffText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [string.Empty];

        var matches = WordDiffTokenRegex.Matches(text);
        if (matches.Count == 0)
            return [text];

        var tokens = new List<string>(matches.Count);
        foreach (Match match in matches)
            tokens.Add(match.Value);

        return tokens;
    }

    private static IReadOnlyList<WordDiffOperation> BuildWordDiffOperations(
        IReadOnlyList<string> leftTokens,
        IReadOnlyList<string> rightTokens)
    {
        var leftCount = leftTokens.Count;
        var rightCount = rightTokens.Count;

        var lcs = new int[leftCount + 1, rightCount + 1];
        for (var i = leftCount - 1; i >= 0; i--)
        {
            for (var j = rightCount - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(leftTokens[i], rightTokens[j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var operations = new List<WordDiffOperation>(leftCount + rightCount);
        var leftIndex = 0;
        var rightIndex = 0;

        while (leftIndex < leftCount && rightIndex < rightCount)
        {
            if (string.Equals(leftTokens[leftIndex], rightTokens[rightIndex], StringComparison.Ordinal))
            {
                operations.Add(new WordDiffOperation(WordDiffOperationKind.Equal, leftTokens[leftIndex]));
                leftIndex++;
                rightIndex++;
                continue;
            }

            if (lcs[leftIndex + 1, rightIndex] >= lcs[leftIndex, rightIndex + 1])
            {
                operations.Add(new WordDiffOperation(WordDiffOperationKind.Remove, leftTokens[leftIndex]));
                leftIndex++;
            }
            else
            {
                operations.Add(new WordDiffOperation(WordDiffOperationKind.Add, rightTokens[rightIndex]));
                rightIndex++;
            }
        }

        while (leftIndex < leftCount)
        {
            operations.Add(new WordDiffOperation(WordDiffOperationKind.Remove, leftTokens[leftIndex]));
            leftIndex++;
        }

        while (rightIndex < rightCount)
        {
            operations.Add(new WordDiffOperation(WordDiffOperationKind.Add, rightTokens[rightIndex]));
            rightIndex++;
        }

        return operations;
    }

    private static void AppendSegment(
        IList<SnapshotDiffTextSegmentViewModel> segments,
        string token,
        string rowKind,
        bool isRightSide,
        bool isChangedChunk)
    {
        if (string.IsNullOrEmpty(token))
            return;

        var (fg, bg, emphasized) = GetSegmentColors(rowKind, isRightSide, isChangedChunk);

        if (segments.Count > 0)
        {
            var last = segments[^1];
            if (string.Equals(last.Foreground, fg, StringComparison.Ordinal)
                && string.Equals(last.Background, bg, StringComparison.Ordinal)
                && last.IsEmphasized == emphasized)
            {
                segments[^1] = new SnapshotDiffTextSegmentViewModel
                {
                    Text = last.Text + token,
                    Foreground = last.Foreground,
                    Background = last.Background,
                    IsEmphasized = last.IsEmphasized
                };
                return;
            }
        }

        segments.Add(new SnapshotDiffTextSegmentViewModel
        {
            Text = token,
            Foreground = fg,
            Background = bg,
            IsEmphasized = emphasized
        });
    }

    private enum WordDiffOperationKind
    {
        Equal,
        Remove,
        Add
    }

    private sealed record WordDiffOperation(WordDiffOperationKind Kind, string Token);
    private static string ResolveRowKind(
        string leftKind,
        string rightKind,
        TextDiffLineDto? left,
        TextDiffLineDto? right)
    {
        if (left is not null && right is not null
            && leftKind == "remove" && rightKind == "add")
            return "modified";

        if (left is not null && leftKind == "remove")
            return "remove";

        if (right is not null && rightKind == "add")
            return "add";

        return "context";
    }

    private static (string Foreground, string Background, bool Emphasized) GetSegmentColors(
        string rowKind,
        bool isRightSide,
        bool isChangedChunk)
    {
        if (rowKind == "add" && isRightSide)
            return ("#CCFFE9", isChangedChunk ? "#205841" : "Transparent", isChangedChunk);

        if (rowKind == "remove" && !isRightSide)
            return ("#FFDCE2", isChangedChunk ? "#6C2B39" : "Transparent", isChangedChunk);

        if (rowKind == "modified")
        {
            if (isChangedChunk)
                return isRightSide
                    ? ("#C6FFEA", "#2A674D", true)
                    : ("#FFE2E8", "#7A3344", true);

            return ("#EAF2FF", "Transparent", false);
        }

        return ("#EAF2FF", "Transparent", false);
    }

    private static string GetBackground(string rowKind, bool isRightSide)
        => rowKind switch
        {
            "add" when isRightSide => "#173E31",
            "remove" when !isRightSide => "#3F1F28",
            "modified" when isRightSide => "#1E4A39",
            "modified" => "#45202B",
            _ => "#142C46"
        };

    private static string GetMarkerForeground(string rowKind, bool isRightSide)
        => rowKind switch
        {
            "add" when isRightSide => "#8DFFD0",
            "remove" when !isRightSide => "#FF9FB0",
            "modified" when isRightSide => "#8DFFD0",
            "modified" => "#FF9FB0",
            _ => "#A9C2DD"
        };

    private static string GetMarker(string rowKind, bool isRightSide)
        => rowKind switch
        {
            "add" when isRightSide => "+",
            "remove" when !isRightSide => "-",
            "modified" when isRightSide => "+",
            "modified" => "-",
            _ => "|"
        };

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

    private void RefreshPinnedHunkHeaderFromRows()
    {
        if (PreviewRows.Count == 0)
        {
            PinnedHunkHeader = string.Empty;
            return;
        }

        if (!string.IsNullOrWhiteSpace(PinnedHunkHeader)
            && PreviewRows.Any(x => x.IsHunkHeader && string.Equals(x.HunkHeader, PinnedHunkHeader, StringComparison.Ordinal)))
        {
            return;
        }

        PinnedHunkHeader = PreviewRows.FirstOrDefault(x => x.IsHunkHeader)?.HunkHeader ?? string.Empty;
    }

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



