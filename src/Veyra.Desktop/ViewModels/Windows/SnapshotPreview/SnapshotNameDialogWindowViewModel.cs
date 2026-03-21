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
using Avalonia.Threading;
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
    private readonly IAudioPreviewPlaybackService _audioPlayback;
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
    private PendingAudioDiffPreviewDto? _lastAudioPreview;
    private byte[] _lastRenderedOverlayPngBytes = Array.Empty<byte>();
    private int _lastRenderedChangedPixelCount;
    private double? _lastRenderedChangedPixelRatio;
    private int _lastRenderedChangedRegionCount;
    private bool _suspendImageDiffRerender;
    private string? _audioPlaybackStatusKey;
    private object[] _audioPlaybackStatusArgs = Array.Empty<object>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _snapshotName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SnapshotTagsSummary))]
    [NotifyPropertyChangedFor(nameof(HasSnapshotTags))]
    private string _snapshotTagsInput = string.Empty;

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
    private string _loadingTitle = string.Empty;

    [ObservableProperty]
    private string _loadingDetail = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeterminateLoadingProgress))]
    [NotifyPropertyChangedFor(nameof(LoadingProgressLabel))]
    private double _loadingProgressValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeterminateLoadingProgress))]
    private bool _isLoadingProgressIndeterminate;

    [ObservableProperty]
    private string _previewSummary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextPreview))]
    [NotifyPropertyChangedFor(nameof(IsBinaryPreview))]
    [NotifyPropertyChangedFor(nameof(IsImagePreview))]
    [NotifyPropertyChangedFor(nameof(IsAudioPreview))]
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
    [NotifyPropertyChangedFor(nameof(HasAudioWaveformPreview))]
    private Bitmap? _audioWaveformPreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAudioSpectrogramPreview))]
    private Bitmap? _audioSpectrogramPreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAudioSpectralDeltaPreview))]
    private Bitmap? _audioSpectralDeltaPreview;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopAudioPlaybackCommand))]
    [NotifyPropertyChangedFor(nameof(HasAudioPlaybackStatus))]
    private bool _isAudioPlaybackActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAudioPlaybackStatus))]
    private string _audioPlaybackStatus = string.Empty;

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
    private bool _showSourceImagePanels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageDiffSettingsToggleLabel))]
    private bool _showImageDiffSettings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageDiffDetailsToggleLabel))]
    private bool _showImageDiffDetails;

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
    public ObservableCollection<AudioChangedSegmentItemViewModel> AudioChangedSegments { get; } = [];

    public SnapshotNameDialogWindowViewModel()
        : this($"{Loc.T("snapshot.default_name_prefix")}_{DateTime.Now:yyyyMMdd_HHmmss}", new AudioPreviewPlaybackService())
    {
    }

    public SnapshotNameDialogWindowViewModel(IAudioPreviewPlaybackService audioPlayback)
        : this($"{Loc.T("snapshot.default_name_prefix")}_{DateTime.Now:yyyyMMdd_HHmmss}", audioPlayback)
    {
    }

    public SnapshotNameDialogWindowViewModel(string defaultName)
        : this(defaultName, new AudioPreviewPlaybackService())
    {
    }

    private SnapshotNameDialogWindowViewModel(string defaultName, IAudioPreviewPlaybackService audioPlayback)
    {
        _audioPlayback = audioPlayback;
        _snapshotName = string.IsNullOrWhiteSpace(defaultName)
            ? $"{Loc.T("snapshot.default_name_prefix")}_{DateTime.Now:yyyyMMdd_HHmmss}"
            : defaultName;

        ChangedFiles.CollectionChanged += OnChangedFilesCollectionChanged;
        PreviewRows.CollectionChanged += OnPreviewRowsCollectionChanged;
        PreviewMetrics.CollectionChanged += OnPreviewMetricsCollectionChanged;
        AudioChangedSegments.CollectionChanged += OnAudioChangedSegmentsCollectionChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        _audioPlayback.PlaybackStateChanged += OnAudioPlaybackStateChanged;
        RefreshImageDiffModes();
        ResetPreview(Loc.T("snapshot.preview.select_changed_file"));
    }

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasChangedFiles => ChangedFiles.Count > 0;
    public bool HasNoChangedFiles => !HasChangedFiles;
    public bool HasSelectedChangedFile => SelectedChangedFile is not null;
    public bool CanSave => !string.IsNullOrWhiteSpace(SnapshotName) && HasChangedFiles;
    public bool HasSnapshotTags => NormalizedSnapshotTags.Count > 0;

    public bool HasPreviewRows => PreviewRows.Count > 0;
    public bool HasPreviewMetrics => PreviewMetrics.Count > 0;
    public bool HasImagePreviews => LeftImagePreview is not null || RightImagePreview is not null;
    public bool HasOverlayImagePreview => OverlayImagePreview is not null;
    public bool HasAnyImagePreview => HasImagePreviews || HasOverlayImagePreview;
    public bool HasNoImagePreviews => !HasAnyImagePreview;
    public bool HasAudioWaveformPreview => AudioWaveformPreview is not null;
    public bool HasAudioSpectrogramPreview => AudioSpectrogramPreview is not null;
    public bool HasAudioSpectralDeltaPreview => AudioSpectralDeltaPreview is not null;
    public bool HasAudioChangedSegments => AudioChangedSegments.Count > 0;
    public bool HasAudioPlaybackStatus => !string.IsNullOrWhiteSpace(AudioPlaybackStatus);
    public bool HasPinnedHunkHeader => !string.IsNullOrWhiteSpace(PinnedHunkHeader);
    public bool HasDeterminateLoadingProgress => !IsLoadingProgressIndeterminate;
    public string LoadingProgressLabel => $"{Math.Clamp(Math.Round(LoadingProgressValue), 0, 100):0}%";

    public bool IsTextPreview => PreviewKind == PendingDiffPreviewKind.Text && HasPreviewRows;
    public bool IsBinaryPreview => PreviewKind == PendingDiffPreviewKind.Binary && HasPreviewMetrics;
    public bool IsImagePreview => PreviewKind == PendingDiffPreviewKind.Image;
    public bool IsAudioPreview => PreviewKind == PendingDiffPreviewKind.Audio;
    public bool IsSplitImageDiffMode => SelectedImageDiffMode?.Mode == ImageDiffVisualizationMode.Split;
    public bool IsHeatmapImageDiffMode => SelectedImageDiffMode?.Mode == ImageDiffVisualizationMode.Heatmap;
    public bool ShowSourceImagePanelsSection => HasImagePreviews && ShowSourceImagePanels;

    public bool HasNoPreviewContent => !IsPreviewLoading && !IsTextPreview && !IsBinaryPreview && !IsImagePreview && !IsAudioPreview;

    public string WrapToggleLabel => IsWrapEnabled ? Loc.T("compare.wrap.on") : Loc.T("compare.wrap.off");
    public string ImageSensitivityLabel => $"{Math.Round(ImageDiffSensitivity):0}%";
    public string ImageDiffCompactSummary => (SelectedImageDiffMode?.Mode ?? ImageDiffVisualizationMode.Overlay) == ImageDiffVisualizationMode.Split
        ? Loc.F("compare.image_quick_summary_split", SelectedImageDiffMode?.Label ?? Loc.T("compare.image_mode.overlay"), ImageSensitivityLabel, SplitPositionLabel)
        : Loc.F("compare.image_quick_summary", SelectedImageDiffMode?.Label ?? Loc.T("compare.image_mode.overlay"), ImageSensitivityLabel);
    public string ImageDiffCompactStateText => $"{ImageRegionBoxesLabel} | {SourceImagePanelsLabel}";
    public string ImageDiffSettingsToggleLabel => ShowImageDiffSettings
        ? Loc.T("compare.hide_diff_settings")
        : Loc.T("compare.show_diff_settings");
    public string ImageDiffDetailsToggleLabel => ShowImageDiffDetails
        ? Loc.T("compare.hide_details")
        : Loc.T("compare.show_details");
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

    public IReadOnlyList<string> NormalizedSnapshotTags => ParseSnapshotTags(SnapshotTagsInput);

    public string SnapshotTagsSummary => HasSnapshotTags
        ? string.Join(", ", NormalizedSnapshotTags.Select(tag => "#" + tag))
        : Loc.T("snapshot_dialog.snapshot_tags_hint");

    public void Initialize(
        string defaultName,
        IReadOnlyCollection<SnapshotPendingFileItemViewModel> changedFiles,
        Func<SnapshotPendingFileItemViewModel, CancellationToken, Task<PendingFileDiffPreviewDto>>? previewLoader = null)
    {
        SnapshotName = string.IsNullOrWhiteSpace(defaultName)
            ? $"{Loc.T("snapshot.default_name_prefix")}_{DateTime.Now:yyyyMMdd_HHmmss}"
            : defaultName;
        SnapshotTagsInput = string.Empty;

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
        OnPropertyChanged(nameof(SnapshotTagsSummary));
        OnPropertyChanged(nameof(HasSnapshotTags));
    }

    public void CleanupPreviewResources()
    {
        _previewCts?.Cancel();
        _imageDiffRenderCts?.Cancel();
        _audioPlayback.Stop();
        ClearAudioPlaybackStatus();
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
        OnPropertyChanged(nameof(SnapshotTagsSummary));
        OnPropertyChanged(nameof(HasSnapshotTags));
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
        OnPropertyChanged(nameof(ImageDiffDetailsToggleLabel));
        RefreshImageDiffModes();
        RefreshAudioPlaybackStatusLocalization();

        var changedFiles = ChangedFiles.ToList();
        var selectedPath = SelectedChangedFile?.RelativePath;
        foreach (var changedFile in changedFiles)
            changedFile.RefreshLocalization();

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
            ApplyBinaryMetrics(_lastBinarySummary, _lastImagePreview, _lastAudioPreview);

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
        _audioPlayback.Stop();
        ClearAudioPlaybackStatus();
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

        var tags = NormalizedSnapshotTags;
        if (tags.Count > 12)
        {
            ErrorMessage = Loc.T("snapshot.error.tags_too_many");
            return;
        }

        SnapshotName = trimmed;
        ErrorMessage = null;
        _audioPlayback.Stop();
        ClearAudioPlaybackStatus();
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

    public byte[] GetCurrentImageDiffPreviewPngBytes()
        => _lastRenderedOverlayPngBytes.Length == 0
            ? Array.Empty<byte>()
            : _lastRenderedOverlayPngBytes.ToArray();

    public async Task RefreshInteractiveImageDiffPreviewAsync(CancellationToken ct = default)
    {
        if (!IsImagePreview || _lastImagePreview is null)
            return;

        await RenderInteractiveImagePreviewAsync(_lastImagePreview, ct);
        ApplyBinaryMetrics(_lastBinarySummary, _lastImagePreview, _lastAudioPreview);
    }

    [RelayCommand]
    private void ToggleShowImageDiffSettings()
    {
        ShowImageDiffSettings = !ShowImageDiffSettings;
    }

    [RelayCommand]
    private void ToggleShowImageDiffDetails()
    {
        ShowImageDiffDetails = !ShowImageDiffDetails;
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
            ShowSourceImagePanels = false;
            ShowImageDiffDetails = false;
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

    [RelayCommand]
    private Task PlayBeforeAudioAsync()
        => PlayAudioPreviewAsync(playAfter: false);

    [RelayCommand]
    private Task PlayAfterAudioAsync()
        => PlayAudioPreviewAsync(playAfter: true);

    [RelayCommand]
    private Task PlayDifferenceAudioAsync()
        => PlayAudioDifferenceAsync();

    [RelayCommand]
    private Task PlayBeforeAudioSegmentAsync(AudioChangedSegmentItemViewModel? segment)
        => PlayAudioSegmentAsync(segment, playAfter: false);

    [RelayCommand]
    private Task PlayAfterAudioSegmentAsync(AudioChangedSegmentItemViewModel? segment)
        => PlayAudioSegmentAsync(segment, playAfter: true);

    [RelayCommand]
    private Task LoopAudioSegmentAsync(AudioChangedSegmentItemViewModel? segment)
        => LoopAudioSegmentCoreAsync(segment);

    [RelayCommand(CanExecute = nameof(IsAudioPlaybackActive))]
    private void StopAudioPlayback()
    {
        _audioPlayback.Stop();
        ClearAudioPlaybackStatus();
    }

    private async Task PlayAudioPreviewAsync(bool playAfter)
    {
        var audioPreview = _lastAudioPreview;
        var audioPath = playAfter
            ? audioPreview?.CurrentAudioPath
            : audioPreview?.BaselineAudioPath;

        if (audioPreview is null || string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return;

        try
        {
            await _audioPlayback.PlayAsync(audioPath);
            SetAudioPlaybackStatus(playAfter ? "compare.audio_playing_after" : "compare.audio_playing_before");
        }
        catch
        {
            ErrorMessage = Loc.T("compare.audio_playback_failed");
        }
    }

    private async Task PlayAudioDifferenceAsync()
    {
        var audioPreview = _lastAudioPreview;
        var audioPath = audioPreview?.DifferenceAudioPath;

        if (audioPreview is null || string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return;

        try
        {
            await _audioPlayback.PlayAsync(audioPath);
            SetAudioPlaybackStatus("compare.audio_playing_difference");
        }
        catch
        {
            ErrorMessage = Loc.T("compare.audio_playback_failed");
        }
    }

    private async Task PlayAudioSegmentAsync(AudioChangedSegmentItemViewModel? segment, bool playAfter)
    {
        var audioPreview = _lastAudioPreview;
        var audioPath = playAfter
            ? audioPreview?.CurrentAudioPath
            : audioPreview?.BaselineAudioPath;

        if (segment is null || audioPreview is null || string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return;

        try
        {
            await _audioPlayback.PlayAsync(
                audioPath,
                TimeSpan.FromSeconds(Math.Max(0d, segment.StartSeconds)),
                TimeSpan.FromSeconds(Math.Max(0.05d, segment.DurationSeconds)));

            SetAudioPlaybackStatus(
                playAfter ? "compare.audio_playing_segment_after" : "compare.audio_playing_segment_before",
                segment.SegmentLabel);
        }
        catch
        {
            ErrorMessage = Loc.T("compare.audio_playback_failed");
        }
    }

    private async Task LoopAudioSegmentCoreAsync(AudioChangedSegmentItemViewModel? segment)
    {
        var audioPreview = _lastAudioPreview;
        var audioPath = audioPreview?.DifferenceAudioPath;

        if (segment is null || audioPreview is null || string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return;

        try
        {
            await _audioPlayback.PlayAsync(
                audioPath,
                TimeSpan.FromSeconds(Math.Max(0d, segment.StartSeconds)),
                TimeSpan.FromSeconds(Math.Max(0.05d, segment.DurationSeconds)),
                loop: true);

            SetAudioPlaybackStatus("compare.audio_looping_segment_difference", segment.SegmentLabel);
        }
        catch
        {
            ErrorMessage = Loc.T("compare.audio_playback_failed");
        }
    }

    private void OnAudioPlaybackStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_audioPlayback.IsPlaying)
                ClearAudioPlaybackStatus();
            else if (!IsAudioPlaybackActive)
                IsAudioPlaybackActive = true;
        });
    }

    private void SetAudioPlaybackStatus(string localizationKey, params object[] args)
    {
        _audioPlaybackStatusKey = localizationKey;
        _audioPlaybackStatusArgs = args;
        AudioPlaybackStatus = args.Length == 0
            ? Loc.T(localizationKey)
            : Loc.F(localizationKey, args);
        IsAudioPlaybackActive = true;
    }

    private void ClearAudioPlaybackStatus()
    {
        _audioPlaybackStatusKey = null;
        _audioPlaybackStatusArgs = Array.Empty<object>();
        AudioPlaybackStatus = string.Empty;
        IsAudioPlaybackActive = false;
    }

    private void RefreshAudioPlaybackStatusLocalization()
    {
        if (IsAudioPlaybackActive && !string.IsNullOrWhiteSpace(_audioPlaybackStatusKey))
        {
            AudioPlaybackStatus = _audioPlaybackStatusArgs.Length == 0
                ? Loc.T(_audioPlaybackStatusKey)
                : Loc.F(_audioPlaybackStatusKey, _audioPlaybackStatusArgs);
        }
        else if (!IsAudioPlaybackActive)
        {
            AudioPlaybackStatus = string.Empty;
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
        SetLoadingState(Loc.T("snapshot.loading.title"), Loc.T("snapshot.loading.fetch"), 14);

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
                    SetLoadingState(Loc.T("snapshot.loading.title"), Loc.T("snapshot.loading.text"), 72);
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
                    SetLoadingState(Loc.T("snapshot.loading.title"), Loc.T("snapshot.loading.binary"), 80);
                    _lastBinarySummary = preview.BinarySummary;
                    _lastImagePreview = null;
                    _lastAudioPreview = null;
                    ApplyBinaryMetrics(preview.BinarySummary, null, null);
                    PreviewKind = PendingDiffPreviewKind.Binary;
                    PreviewSummary = string.IsNullOrWhiteSpace(preview.Message)
                        ? Loc.T("snapshot.preview.binary_ready")
                        : preview.Message;
                    break;
                }
                case PendingDiffPreviewKind.Image:
                {
                    SetLoadingState(Loc.T("snapshot.loading.title"), Loc.T("snapshot.loading.images"), 48);
                    _lastBinarySummary = preview.BinarySummary;
                    _lastImagePreview = preview.ImagePreview;
                    _lastAudioPreview = null;
                    await LoadImagePreviewAsync(preview.ImagePreview, cts.Token);
                    SetLoadingState(Loc.T("snapshot.loading.title"), Loc.T("snapshot.loading.render"), 82);
                    await RenderInteractiveImagePreviewAsync(preview.ImagePreview, cts.Token);
                    SetLoadingState(Loc.T("snapshot.loading.title"), Loc.T("snapshot.loading.finalize"), 96);
                    ApplyBinaryMetrics(preview.BinarySummary, preview.ImagePreview, null);
                    PreviewKind = PendingDiffPreviewKind.Image;
                    PreviewSummary = string.IsNullOrWhiteSpace(preview.Message)
                        ? Loc.T("snapshot.preview.image_ready")
                        : preview.Message;
                    break;
                }
                case PendingDiffPreviewKind.Audio:
                {
                    SetLoadingState(Loc.T("snapshot.loading.title"), Loc.T("snapshot.loading.audio"), 58);
                    _lastBinarySummary = preview.BinarySummary;
                    _lastImagePreview = null;
                    _lastAudioPreview = preview.AudioPreview;
                    await LoadAudioPreviewAsync(preview.AudioPreview, cts.Token);
                    SetLoadingState(Loc.T("snapshot.loading.title"), Loc.T("snapshot.loading.finalize"), 96);
                    ApplyBinaryMetrics(preview.BinarySummary, null, preview.AudioPreview);
                    PreviewKind = PendingDiffPreviewKind.Audio;
                    PreviewSummary = string.IsNullOrWhiteSpace(preview.Message)
                        ? Loc.T("snapshot.preview.audio_ready")
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
            {
                IsPreviewLoading = false;
                ClearLoadingState();
            }
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
            if (imagePreview.IsBaselineTempFile)
                _tempPreviewFiles.Add(imagePreview.BaselineImagePath);
        }

        if (!string.IsNullOrWhiteSpace(imagePreview.CurrentImagePath)
            && File.Exists(imagePreview.CurrentImagePath))
        {
            if (imagePreview.IsCurrentTempFile)
                _tempPreviewFiles.Add(imagePreview.CurrentImagePath);
        }

        if (!string.IsNullOrWhiteSpace(imagePreview.OverlayImagePath)
            && File.Exists(imagePreview.OverlayImagePath)
            && imagePreview.IsOverlayTempFile)
        {
            _tempPreviewFiles.Add(imagePreview.OverlayImagePath);
        }

        var leftImageTask = LoadBitmapAsync(imagePreview.BaselineImagePath, ct);
        var rightImageTask = LoadBitmapAsync(imagePreview.CurrentImagePath, ct);
        await Task.WhenAll(leftImageTask, rightImageTask);
        if (ct.IsCancellationRequested)
            return;

        LeftImagePreview = await leftImageTask;
        RightImagePreview = await rightImageTask;

        LeftImageCaption = BuildImageSideCaption(Loc.T("common.before"), imagePreview.BaselineWidth, imagePreview.BaselineHeight);
        RightImageCaption = BuildImageSideCaption(Loc.T("common.after"), imagePreview.CurrentWidth, imagePreview.CurrentHeight);
    }

    private async Task LoadAudioPreviewAsync(PendingAudioDiffPreviewDto? audioPreview, CancellationToken ct)
    {
        if (audioPreview is null)
            return;

        var waveformTask = LoadBitmapAsync(audioPreview.WaveformImagePath, ct);
        var spectrogramTask = LoadBitmapAsync(audioPreview.SpectrogramImagePath, ct);
        var spectralDeltaTask = LoadBitmapAsync(audioPreview.SpectralDeltaImagePath, ct);

        if (!string.IsNullOrWhiteSpace(audioPreview.WaveformImagePath) && File.Exists(audioPreview.WaveformImagePath))
        {
            if (audioPreview.IsWaveformTempFile)
                _tempPreviewFiles.Add(audioPreview.WaveformImagePath);
        }

        if (audioPreview.IsDifferenceTempFile && !string.IsNullOrWhiteSpace(audioPreview.DifferenceAudioPath))
            _tempPreviewFiles.Add(audioPreview.DifferenceAudioPath);

        if (!string.IsNullOrWhiteSpace(audioPreview.SpectrogramImagePath) && File.Exists(audioPreview.SpectrogramImagePath))
        {
            if (audioPreview.IsSpectrogramTempFile)
                _tempPreviewFiles.Add(audioPreview.SpectrogramImagePath);
        }

        if (!string.IsNullOrWhiteSpace(audioPreview.SpectralDeltaImagePath) && File.Exists(audioPreview.SpectralDeltaImagePath))
        {
            if (audioPreview.IsSpectralDeltaTempFile)
                _tempPreviewFiles.Add(audioPreview.SpectralDeltaImagePath);
        }

        if (!string.IsNullOrWhiteSpace(audioPreview.BaselineAudioPath) && File.Exists(audioPreview.BaselineAudioPath) && audioPreview.IsBaselineTempFile)
            _tempPreviewFiles.Add(audioPreview.BaselineAudioPath);

        if (!string.IsNullOrWhiteSpace(audioPreview.CurrentAudioPath) && File.Exists(audioPreview.CurrentAudioPath) && audioPreview.IsCurrentTempFile)
            _tempPreviewFiles.Add(audioPreview.CurrentAudioPath);

        await Task.WhenAll(waveformTask, spectrogramTask, spectralDeltaTask);
        if (ct.IsCancellationRequested)
            return;

        AudioWaveformPreview = await waveformTask;
        AudioSpectrogramPreview = await spectrogramTask;
        AudioSpectralDeltaPreview = await spectralDeltaTask;
    }

    private async Task ReRenderImageDiffPreviewAsync()
    {
        if (PreviewKind != PendingDiffPreviewKind.Image || _lastImagePreview is null)
            return;

        try
        {
            var delayMs = (SelectedImageDiffMode?.Mode ?? ImageDiffVisualizationMode.Overlay) == ImageDiffVisualizationMode.Split
                ? 18
                : 80;
            await Task.Delay(delayMs);
            await RenderInteractiveImagePreviewAsync(_lastImagePreview, CancellationToken.None);
            ApplyBinaryMetrics(_lastBinarySummary, _lastImagePreview, _lastAudioPreview);
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
        PendingImageDiffPreviewDto? imagePreview,
        PendingAudioDiffPreviewDto? audioPreview)
    {
        PreviewMetrics.Clear();
        AudioChangedSegments.Clear();
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

        if (audioPreview is not null)
        {
            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.duration"),
                $"{FormatAudioDuration(audioPreview.BaselineDurationSeconds)} -> {FormatAudioDuration(audioPreview.CurrentDurationSeconds)}"
                + (audioPreview.HasDurationMismatch ? $" {Loc.T("metric.changed_suffix")}" : string.Empty)));

            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.sample_rate"),
                $"{FormatSampleRate(audioPreview.BaselineSampleRate)} -> {FormatSampleRate(audioPreview.CurrentSampleRate)}"));

            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.channels"),
                $"{FormatChannels(audioPreview.BaselineChannels)} -> {FormatChannels(audioPreview.CurrentChannels)}"));

            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.peak"),
                $"{FormatAmplitude(audioPreview.BaselinePeakAmplitude)} -> {FormatAmplitude(audioPreview.CurrentPeakAmplitude)}"));

            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.rms"),
                $"{FormatAmplitude(audioPreview.BaselineRmsAmplitude)} -> {FormatAmplitude(audioPreview.CurrentRmsAmplitude)}"));

            if (audioPreview.SignalSimilarityRatio.HasValue)
            {
                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.T("metric.audio_similarity"),
                    $"{audioPreview.SignalSimilarityRatio.Value * 100:F1}%"));
            }

            if (audioPreview.SpectralSimilarityRatio.HasValue)
            {
                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.T("metric.spectral_similarity"),
                    $"{audioPreview.SpectralSimilarityRatio.Value * 100:F1}%"));
            }

            if (audioPreview.SpectralDeltaRatio.HasValue)
            {
                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.T("metric.spectral_delta"),
                    $"{audioPreview.SpectralDeltaRatio.Value * 100:F1}%"));
            }

            if (audioPreview.BaselineStereoCorrelation.HasValue || audioPreview.CurrentStereoCorrelation.HasValue)
            {
                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.T("metric.stereo_correlation"),
                    $"{FormatSignedRatio(audioPreview.BaselineStereoCorrelation)} -> {FormatSignedRatio(audioPreview.CurrentStereoCorrelation)}"));
            }

            if (audioPreview.ChangedTimeRatio.HasValue)
            {
                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.T("metric.changed_timeline"),
                    $"{audioPreview.ChangedTimeRatio.Value * 100:F1}%"));
            }

            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.changed_segments"),
                $"{audioPreview.ChangedSegmentCount:N0}"));

            foreach (var bandMetric in audioPreview.BandMetrics)
            {
                var bandName = LocalizeAudioBandName(bandMetric.BandDisplayName);
                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.F("metric.band_energy", bandName),
                    $"{bandMetric.BaselineEnergyRatio * 100:F1}% -> {bandMetric.CurrentEnergyRatio * 100:F1}%"));

                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.F("metric.band_similarity", bandName),
                    $"{bandMetric.SimilarityRatio * 100:F1}% ({Loc.T("metric.delta_short")} {bandMetric.DeltaRatio * 100:F1}%)"));
            }

            foreach (var channelMetric in audioPreview.ChannelMetrics)
            {
                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.F("metric.channel_peak", channelMetric.ChannelDisplayName),
                    $"{FormatAmplitude(channelMetric.BaselinePeakAmplitude)} -> {FormatAmplitude(channelMetric.CurrentPeakAmplitude)}"));

                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.F("metric.channel_rms", channelMetric.ChannelDisplayName),
                    $"{FormatAmplitude(channelMetric.BaselineRmsAmplitude)} -> {FormatAmplitude(channelMetric.CurrentRmsAmplitude)}"));

                if (channelMetric.SimilarityRatio.HasValue)
                {
                    PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                        Loc.F("metric.channel_similarity", channelMetric.ChannelDisplayName),
                        $"{channelMetric.SimilarityRatio.Value * 100:F1}%"));
                }

                if (channelMetric.ChangedTimeRatio.HasValue)
                {
                    PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                        Loc.F("metric.channel_changed_timeline", channelMetric.ChannelDisplayName),
                        $"{channelMetric.ChangedTimeRatio.Value * 100:F1}%"));
                }
            }

            foreach (var segment in audioPreview.ChangedSegments)
            {
                AudioChangedSegments.Add(new AudioChangedSegmentItemViewModel(
                    SegmentLabel: $"#{segment.SegmentIndex}",
                    RangeLabel: $"{FormatPreciseAudioDuration(segment.StartSeconds)} - {FormatPreciseAudioDuration(segment.EndSeconds)}",
                    DurationLabel: FormatPreciseAudioDuration(segment.DurationSeconds),
                    IntensityLabel: $"{segment.AverageDifferenceRatio * 100:F1}% / {segment.PeakDifferenceRatio * 100:F1}%",
                    StartSeconds: segment.StartSeconds,
                    DurationSeconds: segment.DurationSeconds));
            }
            return;
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

            if (imagePreview.IsVectorImage)
            {
                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.T("metric.svg_structure"),
                    $"+{imagePreview.AddedElementCount:N0} / -{imagePreview.RemovedElementCount:N0} / ~{imagePreview.ModifiedElementCount:N0}"));

                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.T("metric.svg_attributes"),
                    $"{imagePreview.ChangedAttributeCount:N0}"));
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

    private static string FormatAudioDuration(double? seconds)
    {
        if (!seconds.HasValue || seconds.Value <= 0d)
            return Loc.T("common.not_available_short");

        var duration = TimeSpan.FromSeconds(seconds.Value);
        return duration.TotalHours >= 1d
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");
    }

    private static string FormatSampleRate(int? sampleRate)
    {
        if (!sampleRate.HasValue || sampleRate.Value <= 0)
            return Loc.T("common.not_available_short");

        return sampleRate.Value >= 1000
            ? $"{sampleRate.Value / 1000d:F1} kHz"
            : $"{sampleRate.Value} Hz";
    }

    private static string FormatChannels(int? channels)
    {
        if (!channels.HasValue || channels.Value <= 0)
            return Loc.T("common.not_available_short");

        return channels.Value switch
        {
            1 => "mono",
            2 => "stereo",
            _ => $"{channels.Value}"
        };
    }

    private static string FormatAmplitude(double? amplitude)
    {
        if (!amplitude.HasValue)
            return Loc.T("common.not_available_short");

        return $"{Math.Clamp(amplitude.Value, 0d, 1d):F3}";
    }

    private static string FormatSignedRatio(double? value)
    {
        if (!value.HasValue)
            return Loc.T("common.not_available_short");

        return $"{Math.Clamp(value.Value, -1d, 1d):+0.00;-0.00;0.00}";
    }

    private static string FormatPreciseAudioDuration(double? seconds)
    {
        if (!seconds.HasValue || seconds.Value <= 0d)
            return "0:00.000";

        var duration = TimeSpan.FromSeconds(seconds.Value);
        return duration.TotalHours >= 1d
            ? duration.ToString(@"h\:mm\:ss\.fff")
            : duration.ToString(@"m\:ss\.fff");
    }

    private static string LocalizeAudioBandName(string? bandName)
        => bandName switch
        {
            "Low" => Loc.T("audio.band.low"),
            "Mids" => Loc.T("audio.band.mids"),
            "Highs" => Loc.T("audio.band.highs"),
            _ => bandName ?? Loc.T("common.not_available_short")
        };

    private void ResetPreview(string message)
    {
        _audioPlayback.Stop();
        ClearAudioPlaybackStatus();
        PreviewKind = PendingDiffPreviewKind.None;
        IsPreviewLoading = false;
        ClearLoadingState();
        PreviewSummary = message;
        PreviewRows.Clear();
        PreviewMetrics.Clear();
        AudioChangedSegments.Clear();
        PinnedHunkHeader = string.Empty;
        _currentTextLines = Array.Empty<TextDiffLineDto>();
        _currentTextHunks = Array.Empty<TextDiffHunkDto>();
        _lastBinarySummary = null;
        _lastImagePreview = null;
        _lastAudioPreview = null;
        _lastRenderedChangedPixelCount = 0;
        _lastRenderedChangedPixelRatio = null;
        _lastRenderedChangedRegionCount = 0;
    }

    private void ReleasePreviewResources()
    {
        _imageDiffRenderCts?.Cancel();
        _audioPlayback.Stop();
        ClearAudioPlaybackStatus();
        ClearLoadingState();
        PreviewRows.Clear();
        PreviewMetrics.Clear();
        AudioChangedSegments.Clear();
        PreviewKind = PendingDiffPreviewKind.None;
        PinnedHunkHeader = string.Empty;
        _currentTextLines = Array.Empty<TextDiffLineDto>();
        _currentTextHunks = Array.Empty<TextDiffHunkDto>();
        _lastBinarySummary = null;
        _lastImagePreview = null;
        _lastAudioPreview = null;
        _lastRenderedChangedPixelCount = 0;
        _lastRenderedChangedPixelRatio = null;
        _lastRenderedChangedRegionCount = 0;

        var leftImage = LeftImagePreview;
        var rightImage = RightImagePreview;
        var overlayImage = OverlayImagePreview;
        var audioWaveform = AudioWaveformPreview;
        var audioSpectrogram = AudioSpectrogramPreview;
        var audioSpectralDelta = AudioSpectralDeltaPreview;

        LeftImagePreview = null;
        RightImagePreview = null;
        OverlayImagePreview = null;
        AudioWaveformPreview = null;
        AudioSpectrogramPreview = null;
        AudioSpectralDeltaPreview = null;

        leftImage?.Dispose();
        rightImage?.Dispose();
        overlayImage?.Dispose();
        audioWaveform?.Dispose();
        audioSpectrogram?.Dispose();
        audioSpectralDelta?.Dispose();

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

    private void SetLoadingState(string title, string detail, double progress, bool indeterminate = false)
    {
        LoadingTitle = title;
        LoadingDetail = detail;
        LoadingProgressValue = Math.Clamp(progress, 0, 100);
        IsLoadingProgressIndeterminate = indeterminate;
    }

    private static Task<Bitmap?> LoadBitmapAsync(string? imagePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return Task.FromResult<Bitmap?>(null);

        return Task.Run(() => (Bitmap?)new Bitmap(imagePath), ct);
    }

    private void ClearLoadingState()
    {
        LoadingTitle = string.Empty;
        LoadingDetail = string.Empty;
        LoadingProgressValue = 0;
        IsLoadingProgressIndeterminate = false;
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

    private void OnAudioChangedSegmentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasAudioChangedSegments));
    }

    private static IReadOnlyList<string> ParseSnapshotTags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = new List<string>();

        foreach (var raw in value.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = raw.Trim().TrimStart('#');
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            if (normalized.Length > 48)
                normalized = normalized[..48].Trim();

            if (normalized.Length == 0 || !seen.Add(normalized))
                continue;

            tags.Add(normalized);
            if (tags.Count >= 12)
                break;
        }

        return tags;
    }
}
