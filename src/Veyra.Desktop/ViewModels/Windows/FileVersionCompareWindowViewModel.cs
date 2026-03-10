using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.Queries.Repository;
using Veyra.Desktop.ViewModels.Pages.Explorer;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class FileVersionCompareWindowViewModel : ObservableObject
{
    private readonly IMediator _mediator;
    private readonly ILogger<FileVersionCompareWindowViewModel> _log;
    private readonly List<string> _tempPreviewFiles = [];

    private CancellationTokenSource? _previewCts;
    private FileVersionCompareListItemViewModel? _leftVersion;
    private FileVersionCompareListItemViewModel? _rightVersion;

    private int _repositoryId;
    private string _repositoryPath = string.Empty;
    private string _relativePath = string.Empty;

    public event Action? RequestClose;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    private string? _errorMessage;

    [ObservableProperty]
    private string _windowTitle = "Compare file versions";

    [ObservableProperty]
    private string _instructionText = "Left click selects LEFT side. Right click selects RIGHT side.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoVersions))]
    private bool _isVersionListLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoPreviewContent))]
    private bool _isPreviewLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextPreview))]
    [NotifyPropertyChangedFor(nameof(IsBinaryPreview))]
    [NotifyPropertyChangedFor(nameof(IsImagePreview))]
    [NotifyPropertyChangedFor(nameof(HasNoPreviewContent))]
    [NotifyPropertyChangedFor(nameof(CanToggleFullFilePreview))]
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WrapToggleLabel))]
    private bool _isWrapEnabled;

    [ObservableProperty] private string _selectedPairSummary = "Select versions in the left panel.";
    [ObservableProperty] private string _previewSummary = "Choose two versions to build preview.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDiffRowsPanel))]
    [NotifyPropertyChangedFor(nameof(ShowFullFilePreviewPanel))]
    [NotifyPropertyChangedFor(nameof(ShowNoDiffPreviewMessage))]
    [NotifyPropertyChangedFor(nameof(ShowNoFullFilePreviewMessage))]
    [NotifyPropertyChangedFor(nameof(CanToggleFullFilePreview))]
    [NotifyPropertyChangedFor(nameof(FullPreviewToggleLabel))]
    private bool _isFullFilePreviewMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoFullFilePreviewMessage))]
    private bool _isFullFilePreviewLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFullFilePreviewContent))]
    [NotifyPropertyChangedFor(nameof(ShowNoFullFilePreviewMessage))]
    private string _fullPreviewBeforeText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFullFilePreviewContent))]
    [NotifyPropertyChangedFor(nameof(ShowNoFullFilePreviewMessage))]
    private string _fullPreviewAfterText = string.Empty;

    [ObservableProperty] private string _fullPreviewSummary = string.Empty;

    public ObservableCollection<FileVersionCompareListItemViewModel> Versions { get; } = [];
    public ObservableCollection<DiffPreviewRowViewModel> PreviewRows { get; } = [];
    public ObservableCollection<SnapshotPreviewMetricItemViewModel> PreviewMetrics { get; } = [];

    public FileVersionCompareWindowViewModel(IMediator mediator, ILogger<FileVersionCompareWindowViewModel> log)
    {
        _mediator = mediator;
        _log = log;

        Versions.CollectionChanged += OnVersionsCollectionChanged;
        PreviewRows.CollectionChanged += OnPreviewRowsCollectionChanged;
        PreviewMetrics.CollectionChanged += OnPreviewMetricsCollectionChanged;
    }

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasVersions => Versions.Count > 0;
    public bool HasNoVersions => !IsVersionListLoading && Versions.Count == 0;

    public bool HasPreviewRows => PreviewRows.Count > 0;
    public bool HasPreviewMetrics => PreviewMetrics.Count > 0;

    public bool IsTextPreview => PreviewKind == PendingDiffPreviewKind.Text && HasPreviewRows;
    public bool IsBinaryPreview => PreviewKind == PendingDiffPreviewKind.Binary && HasPreviewMetrics;
    public bool IsImagePreview => PreviewKind == PendingDiffPreviewKind.Image;

    public bool HasImagePreviews => LeftImagePreview is not null || RightImagePreview is not null;
    public bool HasNoImagePreviews => !HasImagePreviews;

    public bool HasNoPreviewContent => !IsPreviewLoading && !IsTextPreview && !IsBinaryPreview && !IsImagePreview;

    public bool HasFullFilePreviewContent
        => !string.IsNullOrWhiteSpace(FullPreviewBeforeText) || !string.IsNullOrWhiteSpace(FullPreviewAfterText);

    public bool ShowDiffRowsPanel => !IsFullFilePreviewMode && HasPreviewRows;
    public bool ShowNoDiffPreviewMessage => !IsFullFilePreviewMode && !IsPreviewLoading && !HasPreviewRows;
    public bool ShowFullFilePreviewPanel => IsFullFilePreviewMode;
    public bool ShowNoFullFilePreviewMessage => IsFullFilePreviewMode && !IsFullFilePreviewLoading && !HasFullFilePreviewContent;

    public bool CanToggleFullFilePreview
        => !IsPreviewLoading
           && PreviewKind == PendingDiffPreviewKind.Text
           && _leftVersion is not null
           && _rightVersion is not null;

    public string FullPreviewToggleLabel => IsFullFilePreviewMode ? "Show changes only" : "View full file";
    public string WrapToggleLabel => IsWrapEnabled ? "Wrap: On" : "Wrap: Off";

    public bool CanOpenSourceFileOnDisk => GetSourceFilePath() is not null;

    public async Task InitializeAsync(
        int repositoryId,
        string repositoryPath,
        string relativePath,
        string? fileDisplayName,
        long? preferredLeftVersionId,
        long? preferredRightVersionId,
        CancellationToken ct = default)
    {
        _repositoryId = repositoryId;
        _repositoryPath = repositoryPath ?? string.Empty;
        _relativePath = NormalizeRelativePath(relativePath);

        var displayName = string.IsNullOrWhiteSpace(fileDisplayName)
            ? Path.GetFileName(_relativePath)
            : fileDisplayName;

        WindowTitle = string.IsNullOrWhiteSpace(displayName)
            ? "Compare file versions"
            : $"Compare versions - {displayName}";

        SelectedPairSummary = "Loading file versions...";
        ErrorMessage = null;
        IsVersionListLoading = true;

        CleanupPreviewResources();

        Versions.Clear();
        PreviewRows.Clear();
        PreviewMetrics.Clear();
        PreviewKind = PendingDiffPreviewKind.None;
        ResetFullPreviewState();

        try
        {
            var versions = await _mediator.Send(new GetFileVersionHistoryQuery(repositoryId, _relativePath, 500), ct);

            foreach (var version in versions.OrderByDescending(v => v.CreatedAtUtc).ThenByDescending(v => v.FileVersionId))
            {
                Versions.Add(new FileVersionCompareListItemViewModel
                {
                    FileVersionId = version.FileVersionId,
                    CreatedAtUtc = version.CreatedAtUtc,
                    SizeBytes = version.SizeBytes,
                    IsDeletionMarker = version.IsDeletionMarker,
                    HasContentBlocks = version.HasContentBlocks
                });
            }

            if (Versions.Count == 0)
            {
                SelectedPairSummary = "No versions found for this file.";
                PreviewSummary = "Create at least one snapshot to compare versions.";
                return;
            }

            _leftVersion = preferredLeftVersionId is > 0
                ? Versions.FirstOrDefault(v => v.FileVersionId == preferredLeftVersionId.Value)
                : null;

            _rightVersion = preferredRightVersionId is > 0
                ? Versions.FirstOrDefault(v => v.FileVersionId == preferredRightVersionId.Value)
                : null;

            _rightVersion ??= Versions.FirstOrDefault(v => v.IsSelectable);
            _leftVersion ??= Versions.FirstOrDefault(v => v.IsSelectable && (_rightVersion is null || v.FileVersionId != _rightVersion.FileVersionId));

            ApplySelectionStates();
            RefreshSelectedPairSummary();

            if (_leftVersion is not null && _rightVersion is not null && _leftVersion.FileVersionId != _rightVersion.FileVersionId)
                await LoadPreviewAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to initialize version compare window. RepositoryId {RepositoryId}. Path {Path}",
                repositoryId,
                _relativePath);
            ErrorMessage = "Failed to load version comparison data.";
            SelectedPairSummary = "Unable to load file versions.";
        }
        finally
        {
            IsVersionListLoading = false;
            OnPropertyChanged(nameof(CanOpenSourceFileOnDisk));
        }
    }

    public void SelectVersion(FileVersionCompareListItemViewModel item, bool selectRightSide)
    {
        if (item is null)
            return;

        if (!item.IsSelectable)
        {
            ErrorMessage = "Selected version cannot be compared.";
            return;
        }

        ErrorMessage = null;

        if (selectRightSide)
            _rightVersion = item;
        else
            _leftVersion = item;

        ApplySelectionStates();
        RefreshSelectedPairSummary();

        _ = LoadPreviewAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void ToggleWrapMode()
    {
        IsWrapEnabled = !IsWrapEnabled;
    }

    [RelayCommand]
    private async Task ToggleFullFilePreviewAsync()
    {
        if (!CanToggleFullFilePreview)
            return;

        if (IsFullFilePreviewMode)
        {
            IsFullFilePreviewMode = false;
            return;
        }

        await LoadFullFilePreviewAsync();
    }

    [RelayCommand]
    private void OpenSourceFile()
    {
        var fullPath = GetSourceFilePath();
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            ErrorMessage = "Source file path is unavailable.";
            return;
        }

        if (!File.Exists(fullPath))
        {
            ErrorMessage = "Source file is missing on disk.";
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = fullPath, UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenSourceFileInExplorer()
    {
        var fullPath = GetSourceFilePath();
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            ErrorMessage = "Source file path is unavailable.";
            return;
        }

        if (!File.Exists(fullPath))
        {
            ErrorMessage = "Source file is missing on disk.";
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{fullPath}\"",
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void Close()
    {
        CleanupPreviewResources();
        RequestClose?.Invoke();
    }

    public void CleanupPreviewResources()
    {
        _previewCts?.Cancel();
        ReleasePreviewResources();
    }

    private async Task LoadPreviewAsync(CancellationToken externalCt)
    {
        _previewCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _previewCts = cts;

        if (_leftVersion is null || _rightVersion is null)
        {
            ResetPreview("Pick both LEFT and RIGHT versions.");
            return;
        }

        if (_leftVersion.FileVersionId == _rightVersion.FileVersionId)
        {
            ResetPreview("Select two different versions.");
            return;
        }

        ErrorMessage = null;
        IsPreviewLoading = true;
        PreviewSummary = $"Building preview for {_leftVersion.VersionName} -> {_rightVersion.VersionName}...";
        PreviewKind = PendingDiffPreviewKind.None;
        ResetFullPreviewState();
        ReleasePreviewResources();

        try
        {
            OperationResult<PendingFileDiffPreviewDto> result = await _mediator.Send(
                new GetFileVersionDiffPreviewQuery(_leftVersion.FileVersionId, _rightVersion.FileVersionId, 4000),
                cts.Token);

            if (cts.IsCancellationRequested)
                return;

            if (!result.Success || result.Value is null)
            {
                ResetPreview(result.Error ?? "Unable to build preview for selected versions.");
                return;
            }

            var preview = result.Value;
            if (!preview.IsAvailable)
            {
                ResetPreview(preview.Message);
                return;
            }

            switch (preview.Kind)
            {
                case PendingDiffPreviewKind.Text:
                    PreviewRows.Clear();
                    foreach (var row in BuildDiffPreviewRows(preview.Lines, preview.Hunks))
                        PreviewRows.Add(row);

                    PreviewKind = PendingDiffPreviewKind.Text;
                    PreviewSummary = string.IsNullOrWhiteSpace(preview.Message)
                        ? $"{preview.AddedLines} added / {preview.RemovedLines} removed"
                            + (preview.IsTruncated ? " (preview truncated)" : string.Empty)
                        : preview.Message;
                    break;

                case PendingDiffPreviewKind.Binary:
                    ApplyBinaryMetrics(preview.BinarySummary, null);
                    PreviewKind = PendingDiffPreviewKind.Binary;
                    PreviewSummary = string.IsNullOrWhiteSpace(preview.Message)
                        ? "Binary comparison is ready."
                        : preview.Message;
                    break;

                case PendingDiffPreviewKind.Image:
                    await LoadImagePreviewAsync(preview.ImagePreview, cts.Token);
                    ApplyBinaryMetrics(preview.BinarySummary, preview.ImagePreview);
                    PreviewKind = PendingDiffPreviewKind.Image;
                    PreviewSummary = string.IsNullOrWhiteSpace(preview.Message)
                        ? "Image comparison is ready."
                        : preview.Message;
                    break;

                default:
                    ResetPreview("Preview format is not supported.");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to load file-version preview. LeftVersion {LeftVersion}. RightVersion {RightVersion}",
                _leftVersion.FileVersionId,
                _rightVersion.FileVersionId);
            ResetPreview("Unable to load preview for selected versions.");
        }
        finally
        {
            if (!cts.IsCancellationRequested)
                IsPreviewLoading = false;

            OnPropertyChanged(nameof(CanToggleFullFilePreview));
        }
    }

    private async Task LoadFullFilePreviewAsync()
    {
        if (_leftVersion is null || _rightVersion is null)
            return;

        IsFullFilePreviewMode = true;
        IsFullFilePreviewLoading = true;
        FullPreviewBeforeText = string.Empty;
        FullPreviewAfterText = string.Empty;
        FullPreviewSummary = "Loading full file text...";

        var beforeTask = _mediator.Send(new GetFileVersionTextContentQuery(_leftVersion.FileVersionId, 4_000_000));
        var afterTask = _mediator.Send(new GetFileVersionTextContentQuery(_rightVersion.FileVersionId, 4_000_000));

        await Task.WhenAll(beforeTask, afterTask);

        var before = beforeTask.Result;
        var after = afterTask.Result;

        var summaryParts = new List<string>(2);

        if (before.Success && before.Value is not null)
        {
            FullPreviewBeforeText = before.Value.Content;
            summaryParts.Add($"Before: {FormatBytes(before.Value.SizeBytes)}{(before.Value.IsTruncated ? " (truncated)" : string.Empty)}");
        }
        else
        {
            FullPreviewBeforeText = before.Error ?? "Before version text is unavailable.";
            summaryParts.Add("Before unavailable");
        }

        if (after.Success && after.Value is not null)
        {
            FullPreviewAfterText = after.Value.Content;
            summaryParts.Add($"After: {FormatBytes(after.Value.SizeBytes)}{(after.Value.IsTruncated ? " (truncated)" : string.Empty)}");
        }
        else
        {
            FullPreviewAfterText = after.Error ?? "After version text is unavailable.";
            summaryParts.Add("After unavailable");
        }

        FullPreviewSummary = string.Join(" | ", summaryParts);
        IsFullFilePreviewLoading = false;
    }

    private void ApplySelectionStates()
    {
        foreach (var version in Versions)
        {
            version.IsSelectedLeft = _leftVersion is not null && version.FileVersionId == _leftVersion.FileVersionId;
            version.IsSelectedRight = _rightVersion is not null && version.FileVersionId == _rightVersion.FileVersionId;
        }
    }

    private void RefreshSelectedPairSummary()
    {
        if (_leftVersion is null && _rightVersion is null)
        {
            SelectedPairSummary = "Select versions in the list to compare.";
            return;
        }

        if (_leftVersion is null || _rightVersion is null)
        {
            var selected = _leftVersion ?? _rightVersion;
            SelectedPairSummary = selected is null
                ? "Select versions in the list to compare."
                : $"{selected.VersionName} selected. Pick the second side.";
            return;
        }

        if (_leftVersion.FileVersionId == _rightVersion.FileVersionId)
        {
            SelectedPairSummary = "Select two different versions.";
            return;
        }

        SelectedPairSummary = $"LEFT {_leftVersion.VersionName} ({_leftVersion.CreatedAtDisplay})  vs  RIGHT {_rightVersion.VersionName} ({_rightVersion.CreatedAtDisplay})";
    }

    private async Task LoadImagePreviewAsync(PendingImageDiffPreviewDto? imagePreview, CancellationToken ct)
    {
        LeftImageCaption = "Before";
        RightImageCaption = "After";

        if (imagePreview is null)
            return;

        if (!string.IsNullOrWhiteSpace(imagePreview.BaselineImagePath) && File.Exists(imagePreview.BaselineImagePath))
        {
            LeftImagePreview = await Task.Run(() => new Bitmap(imagePreview.BaselineImagePath), ct);
            TrackTempFile(imagePreview.BaselineImagePath, imagePreview.IsBaselineTempFile);
        }

        if (!string.IsNullOrWhiteSpace(imagePreview.CurrentImagePath) && File.Exists(imagePreview.CurrentImagePath))
        {
            RightImagePreview = await Task.Run(() => new Bitmap(imagePreview.CurrentImagePath), ct);
            TrackTempFile(imagePreview.CurrentImagePath, imagePreview.IsCurrentTempFile);
        }

        LeftImageCaption = BuildImageCaption("Before", imagePreview.BaselineWidth, imagePreview.BaselineHeight);
        RightImageCaption = BuildImageCaption("After", imagePreview.CurrentWidth, imagePreview.CurrentHeight);
    }

    private void ApplyBinaryMetrics(PendingBinaryDiffSummaryDto? summary, PendingImageDiffPreviewDto? imagePreview)
    {
        PreviewMetrics.Clear();

        if (summary is null)
            return;

        PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
            "Size",
            $"{FormatBytes(summary.BaselineSizeBytes)} -> {FormatBytes(summary.CurrentSizeBytes)} ({FormatSignedBytes(summary.SizeDeltaBytes)})"));

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

        if (imagePreview is null)
            return;

        var before = imagePreview.BaselineWidth is null || imagePreview.BaselineHeight is null
            ? "n/a"
            : $"{imagePreview.BaselineWidth} x {imagePreview.BaselineHeight}";

        var after = imagePreview.CurrentWidth is null || imagePreview.CurrentHeight is null
            ? "n/a"
            : $"{imagePreview.CurrentWidth} x {imagePreview.CurrentHeight}";

        PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
            "Dimensions",
            $"{before} -> {after}" + (imagePreview.HasDimensionMismatch ? " (changed)" : string.Empty)));
    }

    private static IReadOnlyList<DiffPreviewRowViewModel> BuildDiffPreviewRows(
        IReadOnlyList<TextDiffLineDto> lines,
        IReadOnlyList<TextDiffHunkDto> hunks)
    {
        if (lines.Count == 0)
            return [];

        var rows = new List<DiffPreviewRowViewModel>(lines.Count + (hunks.Count * 3));

        if (hunks.Count > 0)
        {
            foreach (var hunk in hunks.OrderBy(h => h.Sequence))
            {
                var start = Math.Clamp(hunk.StartLineSequence, 0, lines.Count - 1);
                var end = Math.Clamp(hunk.EndLineSequence, start, lines.Count - 1);

                rows.Add(DiffPreviewRowViewModel.CreateHunkHeader(
                    FormatHunkRange(hunk.OldStartLine, hunk.OldLineCount),
                    FormatHunkRange(hunk.NewStartLine, hunk.NewLineCount),
                    NormalizeChangeKindLabel(hunk.ChangeKind)));

                AppendHunkRows(lines, start, end, rows);
            }
        }
        else
        {
            rows.Add(DiffPreviewRowViewModel.CreateHunkHeader("(full)", "(full)", "context"));
            AppendHunkRows(lines, 0, lines.Count - 1, rows);
        }

        return rows;
    }

    private static void AppendHunkRows(
        IReadOnlyList<TextDiffLineDto> lines,
        int startInclusive,
        int endInclusive,
        ICollection<DiffPreviewRowViewModel> rows)
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

    private static DiffPreviewRowViewModel CreatePairedDiffRow(TextDiffLineDto? left, TextDiffLineDto? right)
    {
        var leftKind = NormalizeDiffKind(left?.Kind);
        var rightKind = NormalizeDiffKind(right?.Kind);

        var kindBadge = (leftKind, rightKind) switch
        {
            ("remove", "add") => "~",
            ("remove", _) => "-",
            (_, "add") => "+",
            _ => "="
        };

        return new DiffPreviewRowViewModel
        {
            KindBadge = kindBadge,
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

    private void ResetPreview(string message)
    {
        PreviewKind = PendingDiffPreviewKind.None;
        IsPreviewLoading = false;
        PreviewSummary = message;
        PreviewRows.Clear();
        PreviewMetrics.Clear();
        ResetFullPreviewState();
        OnPropertyChanged(nameof(CanToggleFullFilePreview));
    }

    private void ReleasePreviewResources()
    {
        PreviewRows.Clear();
        PreviewMetrics.Clear();
        PreviewKind = PendingDiffPreviewKind.None;
        ResetFullPreviewState();

        LeftImagePreview?.Dispose();
        RightImagePreview?.Dispose();
        LeftImagePreview = null;
        RightImagePreview = null;

        LeftImageCaption = "Before";
        RightImageCaption = "After";

        foreach (var path in _tempPreviewFiles)
            TryDelete(path);

        _tempPreviewFiles.Clear();
    }

    private void TrackTempFile(string? path, bool isTemp)
    {
        if (isTemp && !string.IsNullOrWhiteSpace(path))
            _tempPreviewFiles.Add(path);
    }

    private void ResetFullPreviewState()
    {
        IsFullFilePreviewMode = false;
        IsFullFilePreviewLoading = false;
        FullPreviewBeforeText = string.Empty;
        FullPreviewAfterText = string.Empty;
        FullPreviewSummary = string.Empty;
    }

    private string? GetSourceFilePath()
    {
        if (string.IsNullOrWhiteSpace(_repositoryPath) || string.IsNullOrWhiteSpace(_relativePath))
            return null;

        var root = Path.GetFullPath(_repositoryPath);
        var rel = _relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, rel));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            return null;

        return fullPath;
    }

    private static string NormalizeRelativePath(string value)
        => value.Replace('\\', '/').Trim();

    private static string BuildImageCaption(string title, int? width, int? height)
        => width is null || height is null ? title : $"{title} - {width} x {height}";

    private static string NormalizeDiffKind(string? kind)
    {
        if (string.Equals(kind, "add", StringComparison.OrdinalIgnoreCase))
            return "add";
        if (string.Equals(kind, "remove", StringComparison.OrdinalIgnoreCase))
            return "remove";
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

    private static string FormatLineNumber(int? lineNumber)
        => lineNumber is int value ? value.ToString("D4") : string.Empty;

    private static string FormatHunkRange(int startLine, int count)
        => count <= 0 ? $"{Math.Max(0, startLine)}" : $"{Math.Max(0, startLine)},{count}";

    private static string FormatRatio(double? value)
        => value.HasValue ? $"{value.Value * 100:F1}%" : "n/a";

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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private void OnVersionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasVersions));
        OnPropertyChanged(nameof(HasNoVersions));
    }

    private void OnPreviewRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasPreviewRows));
        OnPropertyChanged(nameof(IsTextPreview));
        OnPropertyChanged(nameof(HasNoPreviewContent));
        OnPropertyChanged(nameof(ShowDiffRowsPanel));
        OnPropertyChanged(nameof(ShowNoDiffPreviewMessage));
    }

    private void OnPreviewMetricsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasPreviewMetrics));
        OnPropertyChanged(nameof(IsBinaryPreview));
        OnPropertyChanged(nameof(HasNoPreviewContent));
    }
}
