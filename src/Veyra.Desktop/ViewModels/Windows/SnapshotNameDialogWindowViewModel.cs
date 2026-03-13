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
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Preview;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class SnapshotNameDialogWindowViewModel : ObservableObject
{
    private const int ContextCollapseThreshold = 14;
    private const int ContextKeepEdgeLines = 3;
    private static readonly Regex WordDiffTokenRegex = new(@"\w+|\s+|[^\w\s]", RegexOptions.Compiled);
    private readonly LocalizationManager _localization = LocalizationManager.Instance;
    public event Action<bool>? RequestClose;

    private Func<SnapshotPendingFileItemViewModel, CancellationToken, Task<PendingFileDiffPreviewDto>>? _previewLoader;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _imageDiffRenderCts;
    private readonly List<string> _tempPreviewFiles = [];

    private IReadOnlyList<TextDiffLineDto> _currentTextLines = Array.Empty<TextDiffLineDto>();
    private IReadOnlyList<TextDiffHunkDto> _currentTextHunks = Array.Empty<TextDiffHunkDto>();
    private PendingBinaryDiffSummaryDto? _lastBinarySummary;
    private PendingImageDiffPreviewDto? _lastImagePreview;
    private int _lastRenderedChangedPixelCount;
    private double? _lastRenderedChangedPixelRatio;
    private int _lastRenderedChangedRegionCount;
    private bool _suspendImageDiffRerender;

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
    private string _previewSummary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextPreview))]
    [NotifyPropertyChangedFor(nameof(IsBinaryPreview))]
    [NotifyPropertyChangedFor(nameof(IsImagePreview))]
    [NotifyPropertyChangedFor(nameof(HasNoPreviewContent))]
    private PendingDiffPreviewKind _previewKind = PendingDiffPreviewKind.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImagePreviews))]
    [NotifyPropertyChangedFor(nameof(HasAnyImagePreview))]
    [NotifyPropertyChangedFor(nameof(HasNoImagePreviews))]
    [NotifyPropertyChangedFor(nameof(ShowSourceImagePanelsSection))]
    private Bitmap? _leftImagePreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImagePreviews))]
    [NotifyPropertyChangedFor(nameof(HasAnyImagePreview))]
    [NotifyPropertyChangedFor(nameof(HasNoImagePreviews))]
    [NotifyPropertyChangedFor(nameof(ShowSourceImagePanelsSection))]
    private Bitmap? _rightImagePreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverlayImagePreview))]
    [NotifyPropertyChangedFor(nameof(HasAnyImagePreview))]
    [NotifyPropertyChangedFor(nameof(HasNoImagePreviews))]
    private Bitmap? _overlayImagePreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageSensitivityLabel))]
    [NotifyPropertyChangedFor(nameof(ImageDiffCompactSummary))]
    private double _imageDiffSensitivity = 72;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SplitPositionLabel))]
    [NotifyPropertyChangedFor(nameof(ImageDiffCompactSummary))]
    private double _comparisonSplitPercent = 50;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSplitImageDiffMode))]
    [NotifyPropertyChangedFor(nameof(IsHeatmapImageDiffMode))]
    [NotifyPropertyChangedFor(nameof(ImageDiffCompactSummary))]
    private ImageDiffModeOptionViewModel? _selectedImageDiffMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageRegionBoxesLabel))]
    [NotifyPropertyChangedFor(nameof(ImageDiffCompactStateText))]
    private bool _showImageDiffRegionBoxes = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceImagePanelsLabel))]
    [NotifyPropertyChangedFor(nameof(ShowSourceImagePanelsSection))]
    [NotifyPropertyChangedFor(nameof(ImageDiffCompactStateText))]
    private bool _showSourceImagePanels = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageDiffSettingsToggleLabel))]
    private bool _showImageDiffSettings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WrapToggleLabel))]
    private bool _isWrapEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPinnedHunkHeader))]
    private string _pinnedHunkHeader = string.Empty;

    [ObservableProperty] private string _leftImageCaption = "";
    [ObservableProperty] private string _rightImageCaption = "";
    [ObservableProperty] private string _overlayImageCaption = "";

    public ObservableCollection<SnapshotPendingFileItemViewModel> ChangedFiles { get; } = [];
    public ObservableCollection<SnapshotDiffRowItemViewModel> PreviewRows { get; } = [];
    public ObservableCollection<SnapshotPreviewMetricItemViewModel> PreviewMetrics { get; } = [];
    public ObservableCollection<ImageDiffModeOptionViewModel> ImageDiffModes { get; } = [];

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
        _localization.LanguageChanged += OnLanguageChanged;
        RefreshImageDiffModes();
        ResetPreview(Loc.T("snapshot.preview.select_changed_file"));
    }

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasChangedFiles => ChangedFiles.Count > 0;
    public bool HasNoChangedFiles => !HasChangedFiles;
    public bool HasSelectedChangedFile => SelectedChangedFile is not null;
    public bool CanSave => !string.IsNullOrWhiteSpace(SnapshotName) && HasChangedFiles;

    public bool HasPreviewRows => PreviewRows.Count > 0;
    public bool HasPreviewMetrics => PreviewMetrics.Count > 0;
    public bool HasImagePreviews => LeftImagePreview is not null || RightImagePreview is not null;
    public bool HasOverlayImagePreview => OverlayImagePreview is not null;
    public bool HasAnyImagePreview => HasImagePreviews || HasOverlayImagePreview;
    public bool HasNoImagePreviews => !HasAnyImagePreview;
    public bool HasPinnedHunkHeader => !string.IsNullOrWhiteSpace(PinnedHunkHeader);

    public bool IsTextPreview => PreviewKind == PendingDiffPreviewKind.Text && HasPreviewRows;
    public bool IsBinaryPreview => PreviewKind == PendingDiffPreviewKind.Binary && HasPreviewMetrics;
    public bool IsImagePreview => PreviewKind == PendingDiffPreviewKind.Image;
    public bool IsSplitImageDiffMode => SelectedImageDiffMode?.Mode == ImageDiffVisualizationMode.Split;
    public bool IsHeatmapImageDiffMode => SelectedImageDiffMode?.Mode == ImageDiffVisualizationMode.Heatmap;
    public bool ShowSourceImagePanelsSection => HasImagePreviews && ShowSourceImagePanels;

    public bool HasNoPreviewContent => !IsPreviewLoading && !IsTextPreview && !IsBinaryPreview && !IsImagePreview;

    public string WrapToggleLabel => IsWrapEnabled ? Loc.T("compare.wrap.on") : Loc.T("compare.wrap.off");
    public string ImageSensitivityLabel => $"{Math.Round(ImageDiffSensitivity):0}%";
    public string ImageDiffCompactSummary => (SelectedImageDiffMode?.Mode ?? ImageDiffVisualizationMode.Overlay) == ImageDiffVisualizationMode.Split
        ? Loc.F("compare.image_quick_summary_split", SelectedImageDiffMode?.Label ?? Loc.T("compare.image_mode.overlay"), ImageSensitivityLabel, SplitPositionLabel)
        : Loc.F("compare.image_quick_summary", SelectedImageDiffMode?.Label ?? Loc.T("compare.image_mode.overlay"), ImageSensitivityLabel);
    public string ImageDiffCompactStateText => $"{ImageRegionBoxesLabel} · {SourceImagePanelsLabel}";
    public string ImageDiffSettingsToggleLabel => ShowImageDiffSettings
        ? Loc.T("compare.hide_diff_settings")
        : Loc.T("compare.show_diff_settings");
    public string SplitPositionLabel => $"{Math.Round(ComparisonSplitPercent):0}%";
    public string ImageRegionBoxesLabel => ShowImageDiffRegionBoxes
        ? Loc.T("compare.image_regions.on")
        : Loc.T("compare.image_regions.off");
    public string SourceImagePanelsLabel => ShowSourceImagePanels
        ? Loc.T("compare.source_panels.on")
        : Loc.T("compare.source_panels.off");

    public string ChangedFilesCountLabel => HasChangedFiles
        ? Loc.P("snapshot.changed_files.count", ChangedFiles.Count, ChangedFiles.Count)
        : Loc.T("snapshot.changed_files.none");

    public string SelectedChangedFileTitle => SelectedChangedFile?.Name ?? Loc.T("snapshot.selected_file.none_title");

    public string SelectedChangedFilePath => SelectedChangedFile?.RelativePath
        ?? Loc.T("snapshot.selected_file.none_path");

    public string SelectedChangedFileHint => SelectedChangedFile?.ComparisonHint
        ?? Loc.T("snapshot.selected_file.none_hint");

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
            ResetPreview(Loc.T("snapshot.preview.none_for_snapshot"));

        OnPropertyChanged(nameof(HasChangedFiles));
        OnPropertyChanged(nameof(HasNoChangedFiles));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ChangedFilesCountLabel));
    }

    public void CleanupPreviewResources()
    {
        _previewCts?.Cancel();
        _imageDiffRenderCts?.Cancel();
        ReleasePreviewResources();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizationState();
    }

    private void RefreshLocalizationState()
    {
        OnPropertyChanged(nameof(WrapToggleLabel));
        OnPropertyChanged(nameof(ChangedFilesCountLabel));
        OnPropertyChanged(nameof(SelectedChangedFileTitle));
        OnPropertyChanged(nameof(SelectedChangedFilePath));
        OnPropertyChanged(nameof(SelectedChangedFileHint));
        OnPropertyChanged(nameof(ImageSensitivityLabel));
        OnPropertyChanged(nameof(SplitPositionLabel));
        OnPropertyChanged(nameof(ImageRegionBoxesLabel));
        OnPropertyChanged(nameof(SourceImagePanelsLabel));
        OnPropertyChanged(nameof(ImageDiffCompactSummary));
        OnPropertyChanged(nameof(ImageDiffCompactStateText));
        OnPropertyChanged(nameof(ImageDiffSettingsToggleLabel));
        RefreshImageDiffModes();

        var changedFiles = ChangedFiles.ToList();
        var selectedPath = SelectedChangedFile?.RelativePath;
        ChangedFiles.Clear();
        foreach (var changedFile in changedFiles)
            ChangedFiles.Add(changedFile);

        SelectedChangedFile = string.IsNullOrWhiteSpace(selectedPath)
            ? ChangedFiles.FirstOrDefault()
            : ChangedFiles.FirstOrDefault(x => string.Equals(x.RelativePath, selectedPath, StringComparison.OrdinalIgnoreCase));

        RefreshPinnedHunkHeaderFromRows();

        if (SelectedChangedFile is not null && _previewLoader is not null && !IsPreviewLoading)
        {
            _ = LoadPreviewForSelectionAsync(SelectedChangedFile);
            return;
        }

        if (SelectedChangedFile is null)
            PreviewSummary = Loc.T("snapshot.preview.select_changed_file");

        if (_lastBinarySummary is not null)
            ApplyBinaryMetrics(_lastBinarySummary, _lastImagePreview);

        LeftImageCaption = BuildImageSideCaption(Loc.T("common.before"), _lastImagePreview?.BaselineWidth, _lastImagePreview?.BaselineHeight);
        RightImageCaption = BuildImageSideCaption(Loc.T("common.after"), _lastImagePreview?.CurrentWidth, _lastImagePreview?.CurrentHeight);
        OverlayImageCaption = BuildImageOverlayCaption();
    }

    private void RefreshImageDiffModes()
    {
        var selectedMode = SelectedImageDiffMode?.Mode ?? ImageDiffVisualizationMode.Overlay;
        ImageDiffModes.Clear();
        ImageDiffModes.Add(new ImageDiffModeOptionViewModel(ImageDiffVisualizationMode.Overlay, Loc.T("compare.image_mode.overlay")));
        ImageDiffModes.Add(new ImageDiffModeOptionViewModel(ImageDiffVisualizationMode.Heatmap, Loc.T("compare.image_mode.heatmap")));
        ImageDiffModes.Add(new ImageDiffModeOptionViewModel(ImageDiffVisualizationMode.Split, Loc.T("compare.image_mode.split")));
        ImageDiffModes.Add(new ImageDiffModeOptionViewModel(ImageDiffVisualizationMode.Composite, Loc.T("compare.image_mode.composite")));
        SelectedImageDiffMode = ImageDiffModes.FirstOrDefault(x => x.Mode == selectedMode) ?? ImageDiffModes.FirstOrDefault();
        UpdateImageDiffModeSelection();
    }

    partial void OnImageDiffSensitivityChanged(double value)
    {
        if (_suspendImageDiffRerender)
            return;

        _ = ReRenderImageDiffPreviewAsync();
    }

    partial void OnComparisonSplitPercentChanged(double value)
    {
        if (_suspendImageDiffRerender)
            return;

        if (IsSplitImageDiffMode)
            _ = ReRenderImageDiffPreviewAsync();
    }

    partial void OnSelectedImageDiffModeChanged(ImageDiffModeOptionViewModel? value)
    {
        OverlayImageCaption = BuildImageOverlayCaption();
        OnPropertyChanged(nameof(IsSplitImageDiffMode));
        OnPropertyChanged(nameof(IsHeatmapImageDiffMode));
        UpdateImageDiffModeSelection();

        if (_suspendImageDiffRerender)
            return;

        _ = ReRenderImageDiffPreviewAsync();
    }

    [RelayCommand]
    private void SelectImageDiffMode(ImageDiffModeOptionViewModel? option)
    {
        if (option is null)
            return;

        if (ReferenceEquals(SelectedImageDiffMode, option))
        {
            UpdateImageDiffModeSelection();
            return;
        }

        SelectedImageDiffMode = option;
    }

    partial void OnShowImageDiffRegionBoxesChanged(bool value)
    {
        if (_suspendImageDiffRerender)
            return;

        _ = ReRenderImageDiffPreviewAsync();
    }

    partial void OnShowSourceImagePanelsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSourceImagePanelsSection));
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
            ErrorMessage = Loc.T("snapshot.error.no_changes");
            return;
        }

        if (string.IsNullOrWhiteSpace(SnapshotName))
        {
            ErrorMessage = Loc.T("snapshot.error.name_required");
            return;
        }

        var trimmed = SnapshotName.Trim();
        if (trimmed.Length > 256)
        {
            ErrorMessage = Loc.T("snapshot.error.name_too_long");
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
    private void ToggleShowImageDiffRegionBoxes()
    {
        ShowImageDiffRegionBoxes = !ShowImageDiffRegionBoxes;
    }

    [RelayCommand]
    private void ToggleShowSourceImagePanels()
    {
        ShowSourceImagePanels = !ShowSourceImagePanels;
    }

    [RelayCommand]
    private void ToggleShowImageDiffSettings()
    {
        ShowImageDiffSettings = !ShowImageDiffSettings;
    }

    [RelayCommand]
    private async Task ResetImageDiffSettingsAsync()
    {
        _suspendImageDiffRerender = true;
        try
        {
            SelectedImageDiffMode = ImageDiffModes.FirstOrDefault(x => x.Mode == ImageDiffVisualizationMode.Overlay) ?? ImageDiffModes.FirstOrDefault();
            ImageDiffSensitivity = 72;
            ComparisonSplitPercent = 50;
            ShowImageDiffRegionBoxes = true;
            ShowSourceImagePanels = true;
        }
        finally
        {
            _suspendImageDiffRerender = false;
        }

        if (IsImagePreview)
            await ReRenderImageDiffPreviewAsync();
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
            ResetPreview(Loc.T("snapshot.preview.select_changed_file"));
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
        PreviewSummary = Loc.T("snapshot.preview.building");
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
                    _lastBinarySummary = null;
                    _lastImagePreview = null;
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
                    _lastBinarySummary = preview.BinarySummary;
                    _lastImagePreview = null;
                    ApplyBinaryMetrics(preview.BinarySummary, null);
                    PreviewKind = PendingDiffPreviewKind.Binary;
                    PreviewSummary = string.IsNullOrWhiteSpace(preview.Message)
                        ? Loc.T("snapshot.preview.binary_ready")
                        : preview.Message;
                    break;
                }
                case PendingDiffPreviewKind.Image:
                {
                    _lastBinarySummary = preview.BinarySummary;
                    _lastImagePreview = preview.ImagePreview;
                    await LoadImagePreviewAsync(preview.ImagePreview, cts.Token);
                    await RenderInteractiveImagePreviewAsync(preview.ImagePreview, cts.Token);
                    ApplyBinaryMetrics(preview.BinarySummary, preview.ImagePreview);
                    PreviewKind = PendingDiffPreviewKind.Image;
                    PreviewSummary = string.IsNullOrWhiteSpace(preview.Message)
                        ? Loc.T("snapshot.preview.image_ready")
                        : preview.Message;
                    break;
                }
                default:
                    ResetPreview(Loc.T("snapshot.preview.unsupported"));
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            ResetPreview(Loc.T("snapshot.preview.load_failed"));
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
        LeftImageCaption = Loc.T("common.before");
        RightImageCaption = Loc.T("common.after");
        OverlayImageCaption = BuildImageOverlayCaption();

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

        if (!string.IsNullOrWhiteSpace(imagePreview.OverlayImagePath)
            && File.Exists(imagePreview.OverlayImagePath))
        {
            OverlayImagePreview = await Task.Run(() => new Bitmap(imagePreview.OverlayImagePath), ct);
            if (imagePreview.IsOverlayTempFile)
                _tempPreviewFiles.Add(imagePreview.OverlayImagePath);
        }

        LeftImageCaption = BuildImageSideCaption(Loc.T("common.before"), imagePreview.BaselineWidth, imagePreview.BaselineHeight);
        RightImageCaption = BuildImageSideCaption(Loc.T("common.after"), imagePreview.CurrentWidth, imagePreview.CurrentHeight);
    }

    private async Task ReRenderImageDiffPreviewAsync()
    {
        if (PreviewKind != PendingDiffPreviewKind.Image || _lastImagePreview is null)
            return;

        try
        {
            await Task.Delay(120);
            await RenderInteractiveImagePreviewAsync(_lastImagePreview, CancellationToken.None);
            ApplyBinaryMetrics(_lastBinarySummary, _lastImagePreview);
        }
        catch (Exception) when (!(_imageDiffRenderCts?.IsCancellationRequested ?? false))
        {
        }
    }

    private void UpdateImageDiffModeSelection()
    {
        foreach (var option in ImageDiffModes)
            option.IsSelected = ReferenceEquals(option, SelectedImageDiffMode);
    }

    private async Task RenderInteractiveImagePreviewAsync(PendingImageDiffPreviewDto? imagePreview, CancellationToken ct)
    {
        _imageDiffRenderCts?.Cancel();

        if (imagePreview is null
            || string.IsNullOrWhiteSpace(imagePreview.BaselineImagePath)
            || string.IsNullOrWhiteSpace(imagePreview.CurrentImagePath))
        {
            OverlayImagePreview?.Dispose();
            OverlayImagePreview = null;
            _lastRenderedChangedPixelCount = 0;
            _lastRenderedChangedPixelRatio = null;
            _lastRenderedChangedRegionCount = 0;
            return;
        }

        var localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _imageDiffRenderCts = localCts;

        try
        {
            var renderResult = await InteractiveImageDiffRenderer.TryRenderAsync(
                imagePreview.BaselineImagePath,
                imagePreview.CurrentImagePath,
                ImageDiffSensitivity,
                SelectedImageDiffMode?.Mode ?? ImageDiffVisualizationMode.Overlay,
                ComparisonSplitPercent,
                ShowImageDiffRegionBoxes,
                Loc.T("common.before"),
                Loc.T("common.after"),
                localCts.Token);

            if (localCts.IsCancellationRequested)
                return;

            OverlayImagePreview?.Dispose();
            OverlayImagePreview = renderResult?.Bitmap;
            OverlayImageCaption = BuildImageOverlayCaption();
            _lastRenderedChangedPixelCount = renderResult?.ChangedPixelCount ?? 0;
            _lastRenderedChangedPixelRatio = renderResult?.ChangedPixelRatio;
            _lastRenderedChangedRegionCount = renderResult?.ChangedRegionCount ?? 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            OverlayImagePreview?.Dispose();
            OverlayImagePreview = null;
            _lastRenderedChangedPixelCount = 0;
            _lastRenderedChangedPixelRatio = null;
            _lastRenderedChangedRegionCount = 0;
        }
    }

    private static string BuildImageSideCaption(string prefix, int? width, int? height)
    {
        if (width is null || height is null)
            return prefix;

        return $"{prefix} - {width} x {height}";
    }

    private string BuildImageOverlayCaption()
    {
        var modeLabel = SelectedImageDiffMode?.Label ?? Loc.T("compare.image_mode.overlay");
        return $"{Loc.T("compare.overlay")} - {modeLabel}";
    }

    private void ApplyBinaryMetrics(
        PendingBinaryDiffSummaryDto? summary,
        PendingImageDiffPreviewDto? imagePreview)
    {
        PreviewMetrics.Clear();
        if (summary is null)
            return;

        PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
            Loc.T("metric.size"),
            $"{FormatBytes(summary.BaselineSizeBytes)} -> {FormatBytes(summary.CurrentSizeBytes)} ({FormatSignedBytes(summary.SizeDeltaBytes)})"));

        PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
            Loc.T("metric.sha256"),
            $"{Loc.T("common.before").ToLowerInvariant()} {ShortHash(summary.BaselineHashSha256)} | {Loc.T("common.after").ToLowerInvariant()} {ShortHash(summary.CurrentHashSha256)}"));

        PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
            Loc.T("metric.blocks"),
            $"{summary.BaselineBlockCount} -> {summary.CurrentBlockCount}, shared {summary.SharedBlockCount}"));

        PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
            Loc.T("metric.dedup_changed"),
            $"{FormatRatio(summary.DedupRatio)} / {FormatRatio(summary.ChangedBlockRatio)}"));

        if (summary.ByteSimilarityRatio.HasValue)
        {
            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.byte_similarity"),
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
                Loc.T("metric.dimensions"),
                $"{before} -> {after}" + (imagePreview.HasDimensionMismatch ? $" {Loc.T("metric.changed_suffix")}" : string.Empty)));

            if (imagePreview.SimilarityRatio.HasValue)
            {
                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.T("metric.image_similarity"),
                    $"{imagePreview.SimilarityRatio.Value * 100:F1}% (byte-level)"));
            }

            var changedPixelRatio = _lastRenderedChangedPixelRatio ?? imagePreview.ChangedPixelRatio;
            var changedPixelCount = _lastRenderedChangedPixelCount > 0
                ? _lastRenderedChangedPixelCount
                : imagePreview.ChangedPixelCount;
            var changedRegionCount = _lastRenderedChangedRegionCount > 0
                ? _lastRenderedChangedRegionCount
                : imagePreview.ChangedRegionCount;

            if (OverlayImagePreview is not null || changedPixelRatio.HasValue || changedPixelCount > 0)
            {
                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.T("metric.changed_area"),
                    changedPixelRatio.HasValue
                        ? $"{changedPixelRatio.Value * 100:F1}% ({changedPixelCount:N0} px)"
                        : $"{changedPixelCount:N0} px"));

                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.T("metric.changed_regions"),
                    $"{changedRegionCount:N0}"));
            }
        }
    }

    private static string FormatRatio(double? value)
        => value.HasValue ? $"{value.Value * 100:F1}%" : Loc.T("common.not_available_short");

    private static string ShortHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Loc.T("common.not_available_short");

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
        _lastBinarySummary = null;
        _lastImagePreview = null;
        _lastRenderedChangedPixelCount = 0;
        _lastRenderedChangedPixelRatio = null;
        _lastRenderedChangedRegionCount = 0;
    }

    private void ReleasePreviewResources()
    {
        _imageDiffRenderCts?.Cancel();
        PreviewRows.Clear();
        PreviewMetrics.Clear();
        PreviewKind = PendingDiffPreviewKind.None;
        PinnedHunkHeader = string.Empty;
        _currentTextLines = Array.Empty<TextDiffLineDto>();
        _currentTextHunks = Array.Empty<TextDiffHunkDto>();
        _lastRenderedChangedPixelCount = 0;
        _lastRenderedChangedPixelRatio = null;
        _lastRenderedChangedRegionCount = 0;

        LeftImagePreview?.Dispose();
        RightImagePreview?.Dispose();
        OverlayImagePreview?.Dispose();
        LeftImagePreview = null;
        RightImagePreview = null;
        OverlayImagePreview = null;

        LeftImageCaption = Loc.T("common.before");
        RightImageCaption = Loc.T("common.after");
        OverlayImageCaption = BuildImageOverlayCaption();

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



