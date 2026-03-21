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
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Commands.Repository;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.Queries.Repository;
using Veyra.Application.Services.Diff;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Preview;
using Veyra.Desktop.ViewModels.Pages.Explorer;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class FileVersionCompareWindowViewModel : ObservableObject
{
    private readonly IMediator _mediator;
    private readonly ILogger<FileVersionCompareWindowViewModel> _log;
    private readonly INativeWordCompareService _nativeWordCompare;
    private readonly IAudioPreviewPlaybackService _audioPlayback;
    private readonly LocalizationManager _localization = LocalizationManager.Instance;
    private readonly List<string> _tempPreviewFiles = [];
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _imageDiffRenderCts;
    private FileVersionCompareListItemViewModel? _leftVersion;
    private FileVersionCompareListItemViewModel? _rightVersion;
    private bool _isWordSemanticPreview;
    private PendingBinaryDiffSummaryDto? _lastBinarySummary;
    private PendingImageDiffPreviewDto? _lastImagePreview;
    private PendingAudioDiffPreviewDto? _lastAudioPreview;
    private byte[] _lastRenderedOverlayPngBytes = Array.Empty<byte>();
    private int _lastRenderedChangedPixelCount;
    private double? _lastRenderedChangedPixelRatio;
    private int _lastRenderedChangedRegionCount;
    private bool _suspendImageDiffRerender;

    private int _repositoryId;
    private string _repositoryPath = string.Empty;
    private string _relativePath = string.Empty;
    private string _displayName = string.Empty;
    private string? _audioPlaybackStatusKey;
    private object[] _audioPlaybackStatusArgs = Array.Empty<object>();

    public event Action? RequestClose;
    public event Action? RequestSaveImageDiffPreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    private string? _errorMessage;

    [ObservableProperty]
    private string _windowTitle = string.Empty;

    [ObservableProperty]
    private string _instructionText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoVersions))]
    private bool _isVersionListLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoPreviewContent))]
    [NotifyPropertyChangedFor(nameof(IsBusyOverlayVisible))]
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
    [NotifyPropertyChangedFor(nameof(IsBusyOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(CanOpenNativeWordCompare))]
    private bool _isOpeningNativeWordCompare;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextPreview))]
    [NotifyPropertyChangedFor(nameof(IsBinaryPreview))]
    [NotifyPropertyChangedFor(nameof(IsImagePreview))]
    [NotifyPropertyChangedFor(nameof(IsAudioPreview))]
    [NotifyPropertyChangedFor(nameof(IsWordRichPreview))]
    [NotifyPropertyChangedFor(nameof(ShowDiffRowsPanel))]
    [NotifyPropertyChangedFor(nameof(ShowWordDiffRowsPanel))]
    [NotifyPropertyChangedFor(nameof(ShowNoDiffPreviewMessage))]
    [NotifyPropertyChangedFor(nameof(HasNoPreviewContent))]
    [NotifyPropertyChangedFor(nameof(CanToggleFullFilePreview))]
    private PendingDiffPreviewKind _previewKind = PendingDiffPreviewKind.None;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveImageDiffPreviewCommand))]
    [NotifyPropertyChangedFor(nameof(HasImagePreviews))]
    [NotifyPropertyChangedFor(nameof(HasAnyImagePreview))]
    [NotifyPropertyChangedFor(nameof(HasNoImagePreviews))]
    [NotifyPropertyChangedFor(nameof(ShowSourceImagePanelsSection))]
    private Bitmap? _leftImagePreview;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveImageDiffPreviewCommand))]
    [NotifyPropertyChangedFor(nameof(HasImagePreviews))]
    [NotifyPropertyChangedFor(nameof(HasAnyImagePreview))]
    [NotifyPropertyChangedFor(nameof(HasNoImagePreviews))]
    [NotifyPropertyChangedFor(nameof(ShowSourceImagePanelsSection))]
    private Bitmap? _rightImagePreview;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveImageDiffPreviewCommand))]
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

    [ObservableProperty] private string _leftImageCaption = string.Empty;
    [ObservableProperty] private string _rightImageCaption = string.Empty;
    [ObservableProperty] private string _overlayImageCaption = string.Empty;

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
    private bool _showSourceImagePanels = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageDiffSettingsToggleLabel))]
    private bool _showImageDiffSettings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageDiffDetailsToggleLabel))]
    private bool _showImageDiffDetails;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WrapToggleLabel))]
    private bool _isWrapEnabled;

    [ObservableProperty] private string _selectedPairSummary = string.Empty;
    [ObservableProperty] private string _previewSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDiffRowsPanel))]
    [NotifyPropertyChangedFor(nameof(ShowWordDiffRowsPanel))]
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
    public ObservableCollection<WordSemanticDiffRowViewModel> WordPreviewRows { get; } = [];
    public ObservableCollection<SnapshotPreviewMetricItemViewModel> PreviewMetrics { get; } = [];
    public ObservableCollection<ImageDiffModeOptionViewModel> ImageDiffModes { get; } = [];
    public ObservableCollection<AudioChangedSegmentItemViewModel> AudioChangedSegments { get; } = [];

    public FileVersionCompareWindowViewModel(
        IMediator mediator,
        ILogger<FileVersionCompareWindowViewModel> log,
        INativeWordCompareService nativeWordCompare,
        IAudioPreviewPlaybackService audioPlayback)
    {
        _mediator = mediator;
        _log = log;
        _nativeWordCompare = nativeWordCompare;
        _audioPlayback = audioPlayback;

        Versions.CollectionChanged += OnVersionsCollectionChanged;
        PreviewRows.CollectionChanged += OnPreviewRowsCollectionChanged;
        WordPreviewRows.CollectionChanged += OnWordPreviewRowsCollectionChanged;
        PreviewMetrics.CollectionChanged += OnPreviewMetricsCollectionChanged;
        AudioChangedSegments.CollectionChanged += OnAudioChangedSegmentsCollectionChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        _audioPlayback.PlaybackStateChanged += OnAudioPlaybackStateChanged;
        RefreshLocalizationState();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizationState();
    }

    private void RefreshLocalizationState()
    {
        WindowTitle = BuildWindowTitle();
        InstructionText = Loc.T("compare.instructions");
        OnPropertyChanged(nameof(FullPreviewToggleLabel));
        OnPropertyChanged(nameof(WrapToggleLabel));
        OnPropertyChanged(nameof(NativeWordCompareHint));
        OnPropertyChanged(nameof(NativeWordCompareFormattingHint));
        OnPropertyChanged(nameof(ImageSensitivityLabel));
        OnPropertyChanged(nameof(SplitPositionLabel));
        OnPropertyChanged(nameof(ImageRegionBoxesLabel));
        OnPropertyChanged(nameof(SourceImagePanelsLabel));
        OnPropertyChanged(nameof(ImageDiffCompactSummary));
        OnPropertyChanged(nameof(ImageDiffCompactStateText));
        OnPropertyChanged(nameof(ImageDiffSettingsToggleLabel));
        RefreshImageDiffModes();
        RefreshAudioPlaybackStatusLocalization();

        RefreshVersionsBindings();
        RefreshSelectedPairSummary();

        if (_leftVersion is not null && _rightVersion is not null && !IsPreviewLoading)
        {
            if (IsFullFilePreviewMode)
                _ = LoadFullFilePreviewAsync();
            else
                _ = LoadPreviewAsync(CancellationToken.None);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(PreviewSummary))
                PreviewSummary = Loc.T("compare.choose_versions");
            LeftImageCaption = BuildImageCaption(Loc.T("common.before"), null, null);
            RightImageCaption = BuildImageCaption(Loc.T("common.after"), null, null);
            OverlayImageCaption = Loc.T("compare.overlay");
        }
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

    private string BuildWindowTitle()
    {
        return string.IsNullOrWhiteSpace(_displayName)
            ? Loc.T("compare.window_title")
            : Loc.F("compare.window_title_with_name", _displayName);
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
            RebuildPreviewMetrics();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Interactive image preview re-render failed.");
        }
    }

    private void UpdateImageDiffModeSelection()
    {
        foreach (var option in ImageDiffModes)
            option.IsSelected = ReferenceEquals(option, SelectedImageDiffMode);
    }

    private void RefreshVersionsBindings()
    {
        if (Versions.Count == 0)
            return;

        var leftId = _leftVersion?.FileVersionId;
        var rightId = _rightVersion?.FileVersionId;
        var versions = Versions.ToList();
        foreach (var version in versions)
            version.RefreshLocalization();

        _leftVersion = leftId is > 0
            ? Versions.FirstOrDefault(x => x.FileVersionId == leftId.Value)
            : null;
        _rightVersion = rightId is > 0
            ? Versions.FirstOrDefault(x => x.FileVersionId == rightId.Value)
            : null;
        ApplySelectionStates();
    }

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasVersions => Versions.Count > 0;
    public bool HasNoVersions => !IsVersionListLoading && Versions.Count == 0;

    public bool HasPreviewRows => PreviewRows.Count > 0;
    public bool HasWordPreviewRows => WordPreviewRows.Count > 0;
    public bool HasPreviewMetrics => PreviewMetrics.Count > 0;
    public bool IsBusyOverlayVisible => IsPreviewLoading || IsOpeningNativeWordCompare;
    public bool HasDeterminateLoadingProgress => !IsLoadingProgressIndeterminate;
    public string LoadingProgressLabel => $"{Math.Clamp(Math.Round(LoadingProgressValue), 0, 100):0}%";
    public bool IsTextPreview => PreviewKind == PendingDiffPreviewKind.Text && (HasPreviewRows || HasWordPreviewRows);
    public bool IsWordRichPreview => IsTextPreview && _isWordSemanticPreview && HasWordPreviewRows;
    public bool IsBinaryPreview => PreviewKind == PendingDiffPreviewKind.Binary && HasPreviewMetrics;
    public bool IsImagePreview => PreviewKind == PendingDiffPreviewKind.Image;
    public bool IsAudioPreview => PreviewKind == PendingDiffPreviewKind.Audio;
    public bool HasImagePreviews => LeftImagePreview is not null || RightImagePreview is not null;
    public bool HasOverlayImagePreview => OverlayImagePreview is not null;
    public bool HasAnyImagePreview => HasImagePreviews || HasOverlayImagePreview;
    public bool HasNoImagePreviews => !HasAnyImagePreview;
    public bool HasAudioWaveformPreview => AudioWaveformPreview is not null;
    public bool HasAudioSpectrogramPreview => AudioSpectrogramPreview is not null;
    public bool HasAudioSpectralDeltaPreview => AudioSpectralDeltaPreview is not null;
    public bool HasAudioChangedSegments => AudioChangedSegments.Count > 0;
    public bool HasAudioPlaybackStatus => !string.IsNullOrWhiteSpace(AudioPlaybackStatus);
    public bool HasNoPreviewContent => !IsPreviewLoading && !IsTextPreview && !IsBinaryPreview && !IsImagePreview && !IsAudioPreview;
    public bool IsSplitImageDiffMode => SelectedImageDiffMode?.Mode == ImageDiffVisualizationMode.Split;
    public bool IsHeatmapImageDiffMode => SelectedImageDiffMode?.Mode == ImageDiffVisualizationMode.Heatmap;
    public bool ShowSourceImagePanelsSection => HasImagePreviews && ShowSourceImagePanels;
    public bool CanSaveImageDiffPreview => IsImagePreview && HasOverlayImagePreview && _lastRenderedOverlayPngBytes.Length > 0;
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
    public bool HasFullFilePreviewContent
        => !string.IsNullOrWhiteSpace(FullPreviewBeforeText) || !string.IsNullOrWhiteSpace(FullPreviewAfterText);
    public bool ShowDiffRowsPanel => !IsFullFilePreviewMode && HasPreviewRows && !IsWordRichPreview;
    public bool ShowWordDiffRowsPanel => !IsFullFilePreviewMode && IsWordRichPreview && HasWordPreviewRows;
    public bool ShowNoDiffPreviewMessage
        => !IsFullFilePreviewMode
           && !IsPreviewLoading
           && !HasPreviewRows
           && !HasWordPreviewRows
           && !IsBinaryPreview
           && !IsImagePreview
           && !IsAudioPreview;
    public bool ShowFullFilePreviewPanel => IsFullFilePreviewMode && !IsWordRichPreview;
    public bool ShowNoFullFilePreviewMessage => IsFullFilePreviewMode && !IsFullFilePreviewLoading && !HasFullFilePreviewContent;
    public bool CanToggleFullFilePreview
        => !IsPreviewLoading
           && PreviewKind == PendingDiffPreviewKind.Text
           && !_isWordSemanticPreview
           && !IsNativeWordPreferredForPreview
           && _leftVersion is not null
           && _rightVersion is not null;

    public string FullPreviewToggleLabel => IsFullFilePreviewMode
        ? Loc.T("compare.full_preview.show_changes_only")
        : Loc.T("compare.full_preview.view_full_file");
    public string WrapToggleLabel => IsWrapEnabled ? Loc.T("compare.wrap.on") : Loc.T("compare.wrap.off");

    public bool CanOpenSourceFileOnDisk => GetSourceFilePath() is not null;

    public bool IsWordDocument => WordSemanticProjection.IsWordOoxmlExtension(Path.GetExtension(_relativePath));

    public bool CanOpenNativeWordCompare
        => IsWordDocument
           && !IsOpeningNativeWordCompare
           && _leftVersion is not null
           && _rightVersion is not null
           && _leftVersion.FileVersionId != _rightVersion.FileVersionId;

    public bool IsNativeWordPreferredForPreview
        => IsWordDocument && _nativeWordCompare.IsAvailable;

    public string NativeWordCompareHint => _nativeWordCompare.IsAvailable
        ? Loc.T("compare.native_word.hint")
        : Loc.T("compare.native_word.required");

    public string NativeWordCompareFormattingHint => _nativeWordCompare.IsAvailable
        ? Loc.T("compare.native_word.hint_formatting")
        : Loc.T("compare.native_word.required");

    public async Task InitializeAsync(
        int repositoryId,
        string repositoryPath,
        string relativePath,
        string? fileDisplayName,
        long? preferredLeftVersionId,
        long? preferredRightVersionId,
        CancellationToken ct = default)
    {
        var scheduleInitialPreviewLoad = false;
        long initialLeftVersionId = 0;
        long initialRightVersionId = 0;

        _repositoryId = repositoryId;
        _repositoryPath = repositoryPath ?? string.Empty;
        _relativePath = NormalizeRelativePath(relativePath);

        var displayName = string.IsNullOrWhiteSpace(fileDisplayName)
            ? Path.GetFileName(_relativePath)
            : fileDisplayName;
        _displayName = displayName ?? string.Empty;

        WindowTitle = BuildWindowTitle();

        SelectedPairSummary = Loc.T("compare.loading_versions");
        ErrorMessage = null;
        IsVersionListLoading = true;

        CleanupPreviewResources();
        SetLoadingState(Loc.T("compare.loading.title"), Loc.T("compare.loading.fetch_versions"), 8);

        Versions.Clear();
        PreviewRows.Clear();
        WordPreviewRows.Clear();
        PreviewMetrics.Clear();
        PreviewKind = PendingDiffPreviewKind.None;
        PreviewSummary = Loc.T("compare.preview.pick_both");
        _isWordSemanticPreview = false;
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
                SelectedPairSummary = Loc.T("compare.no_versions");
                PreviewSummary = Loc.T("compare.create_snapshot_to_compare");
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
            {
                scheduleInitialPreviewLoad = true;
                initialLeftVersionId = _leftVersion.FileVersionId;
                initialRightVersionId = _rightVersion.FileVersionId;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to initialize version compare window. RepositoryId {RepositoryId}. Path {Path}",
                repositoryId,
                _relativePath);
            ErrorMessage = Loc.T("compare.load_failed");
            SelectedPairSummary = Loc.T("compare.unable_to_load_versions");
        }
        finally
        {
            IsVersionListLoading = false;
            if (!scheduleInitialPreviewLoad || ct.IsCancellationRequested)
                ClearLoadingState();
            OnPropertyChanged(nameof(CanOpenSourceFileOnDisk));
            OnPropertyChanged(nameof(IsWordDocument));
            OnPropertyChanged(nameof(CanOpenNativeWordCompare));
            OnPropertyChanged(nameof(IsNativeWordPreferredForPreview));
            OnPropertyChanged(nameof(NativeWordCompareHint));
            OnPropertyChanged(nameof(NativeWordCompareFormattingHint));
        }

        if (scheduleInitialPreviewLoad && !ct.IsCancellationRequested)
            _ = QueueInitialPreviewLoadAsync(initialLeftVersionId, initialRightVersionId, ct);
    }

    public void SelectVersion(FileVersionCompareListItemViewModel item, bool selectRightSide)
    {
        if (item is null)
            return;

        if (!item.IsSelectable)
        {
            ErrorMessage = Loc.T("compare.version.not_comparable");
            return;
        }

        ErrorMessage = null;

        if (selectRightSide)
            _rightVersion = item;
        else
            _leftVersion = item;

        ApplySelectionStates();
        RefreshSelectedPairSummary();
        OnPropertyChanged(nameof(CanOpenNativeWordCompare));
        OnPropertyChanged(nameof(IsNativeWordPreferredForPreview));

        _ = LoadPreviewAsync(CancellationToken.None);
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

    public void HideImageDiffSettingsPane()
    {
        ShowImageDiffSettings = false;
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

    [RelayCommand(CanExecute = nameof(CanSaveImageDiffPreview))]
    private void SaveImageDiffPreview()
    {
        RequestSaveImageDiffPreview?.Invoke();
    }

    [RelayCommand]
    private async Task ToggleFullFilePreviewAsync()
    {
        if (!CanToggleFullFilePreview)
            return;

        if (IsFullFilePreviewMode)
        {
            IsFullFilePreviewMode = false;
            await LoadPreviewAsync(CancellationToken.None);
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
            ErrorMessage = Loc.T("compare.source_file.unavailable");
            return;
        }

        if (!File.Exists(fullPath))
        {
            ErrorMessage = Loc.T("compare.source_file.missing");
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
            ErrorMessage = Loc.T("compare.source_file.unavailable");
            return;
        }

        if (!File.Exists(fullPath))
        {
            ErrorMessage = Loc.T("compare.source_file.missing");
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
    private Task OpenNativeWordCompareAsync()
        => OpenNativeWordCompareCoreAsync(NativeWordCompareOptions.ContentOnly, Loc.T("compare.native_word.opened_content"));

    [RelayCommand]
    private Task OpenNativeWordCompareWithFormattingAsync()
        => OpenNativeWordCompareCoreAsync(NativeWordCompareOptions.WithFormatting, Loc.T("compare.native_word.opened_formatting"));

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

    private async Task OpenNativeWordCompareCoreAsync(NativeWordCompareOptions options, string successMessage)
    {
        if (!CanOpenNativeWordCompare || _leftVersion is null || _rightVersion is null)
            return;

        if (!_nativeWordCompare.IsAvailable)
        {
            ErrorMessage = Loc.T("compare.native_word.required");
            return;
        }

        ErrorMessage = null;
        IsOpeningNativeWordCompare = true;
        SetLoadingState(Loc.T("compare.word_loading_title"), Loc.T("compare.word_loading_prepare_left"), 20);

        var extension = Path.GetExtension(_relativePath);
        var tempRoot = Path.Combine(Path.GetTempPath(), "VeyraFlow", "word-native-compare");
        Directory.CreateDirectory(tempRoot);
        CleanupStaleNativeWordCompareFiles(tempRoot);

        var leftTemp = Path.Combine(tempRoot, $"{Guid.NewGuid():N}.left{extension}");
        var rightTemp = Path.Combine(tempRoot, $"{Guid.NewGuid():N}.right{extension}");

        try
        {
            var leftRestore = await _mediator.Send(new RestoreFileVersionCommand(
                _repositoryId,
                _relativePath,
                _leftVersion.FileVersionId,
                OverwriteCurrent: false,
                TargetPath: leftTemp));

            if (!leftRestore.Success || string.IsNullOrWhiteSpace(leftRestore.Value))
            {
                ErrorMessage = UserFacingMessageLocalizer.LocalizeOrFallback(leftRestore.Error, "compare.native_word.prepare_left_failed");
                return;
            }

            SetLoadingState(Loc.T("compare.word_loading_title"), Loc.T("compare.word_loading_prepare_right"), 52);

            var rightRestore = await _mediator.Send(new RestoreFileVersionCommand(
                _repositoryId,
                _relativePath,
                _rightVersion.FileVersionId,
                OverwriteCurrent: false,
                TargetPath: rightTemp));

            if (!rightRestore.Success || string.IsNullOrWhiteSpace(rightRestore.Value))
            {
                ErrorMessage = UserFacingMessageLocalizer.LocalizeOrFallback(rightRestore.Error, "compare.native_word.prepare_right_failed");
                return;
            }

            SetLoadingState(Loc.T("compare.word_loading_title"), Loc.T("compare.word_loading_launch"), 84, indeterminate: true);
            var launch = await _nativeWordCompare.OpenCompareAsync(leftRestore.Value, rightRestore.Value, options);
            if (!launch.Success)
            {
                ErrorMessage = UserFacingMessageLocalizer.LocalizeOrFallback(launch.ErrorMessage, "compare.native_word.open_failed");
                return;
            }

            PreviewSummary = successMessage;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to open native Word compare. RepositoryId {RepositoryId}. LeftVersion {LeftVersion}. RightVersion {RightVersion}",
                _repositoryId,
                _leftVersion.FileVersionId,
                _rightVersion.FileVersionId);

            ErrorMessage = Loc.T("compare.native_word.launch_failed");
        }
        finally
        {
            IsOpeningNativeWordCompare = false;
            ClearLoadingState();
            OnPropertyChanged(nameof(IsNativeWordPreferredForPreview));
        }
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
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to start audio preview playback. Path {Path}", audioPath);
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
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to start audio difference playback. Path {Path}", audioPath);
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
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to start audio segment playback. Path {Path}. Segment {Segment}", audioPath, segment.SegmentLabel);
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
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to start audio segment loop playback. Path {Path}. Segment {Segment}", audioPath, segment.SegmentLabel);
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

    [RelayCommand]
    private void Close()
    {
        _audioPlayback.Stop();
        ClearAudioPlaybackStatus();
        RequestClose?.Invoke();
    }

    public byte[] GetCurrentImageDiffPreviewPngBytes()
        => _lastRenderedOverlayPngBytes.Length == 0
            ? Array.Empty<byte>()
            : _lastRenderedOverlayPngBytes.ToArray();

    public DetachedImagePreviewRequest? BuildDetachedImagePreviewRequest()
    {
        if (!IsImagePreview)
            return null;

        var overlayBytes = GetCurrentImageDiffPreviewPngBytes();
        var leftPath = _lastImagePreview?.BaselineImagePath;
        var rightPath = _lastImagePreview?.CurrentImagePath;
        var hasSplitSource = !string.IsNullOrWhiteSpace(leftPath) && !string.IsNullOrWhiteSpace(rightPath);

        if (overlayBytes.Length == 0 && !hasSplitSource)
            return null;

        var previewTitle = string.IsNullOrWhiteSpace(OverlayImageCaption)
            ? Loc.T("compare.window_title")
            : OverlayImageCaption;

        return new DetachedImagePreviewRequest
        {
            Title = previewTitle,
            LeftImagePath = leftPath,
            RightImagePath = rightPath,
            OverlayPngBytes = overlayBytes,
            IsSplitMode = IsSplitImageDiffMode && hasSplitSource,
            InitialSplitPercent = ComparisonSplitPercent
        };
    }

    public string BuildSuggestedImageDiffFileName()
    {
        var baseName = string.IsNullOrWhiteSpace(_displayName)
            ? "image-diff"
            : Path.GetFileNameWithoutExtension(_displayName);

        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "image-diff";

        var modeSuffix = (SelectedImageDiffMode?.Mode ?? ImageDiffVisualizationMode.Overlay) switch
        {
            ImageDiffVisualizationMode.Heatmap => "heatmap",
            ImageDiffVisualizationMode.Split => "split",
            ImageDiffVisualizationMode.Composite => "composite",
            _ => "overlay"
        };

        return $"{baseName}-diff-{modeSuffix}.png";
    }

    public async Task SaveCurrentImageDiffPreviewAsync(string outputPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            return;

        if (_lastImagePreview is not null)
        {
            var renderResult = await TryRenderCurrentImageDiffAsync(ct);
            if (renderResult is not null)
            {
                _lastRenderedOverlayPngBytes = renderResult.PngBytes;
                _lastRenderedChangedPixelCount = renderResult.ChangedPixelCount;
                _lastRenderedChangedPixelRatio = renderResult.ChangedPixelRatio;
                _lastRenderedChangedRegionCount = renderResult.ChangedRegionCount;
            }
        }

        if (_lastRenderedOverlayPngBytes.Length == 0)
        {
            ErrorMessage = Loc.T("compare.image_save_missing");
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllBytesAsync(outputPath, _lastRenderedOverlayPngBytes, ct);
            ErrorMessage = null;
            PreviewSummary = Loc.F("compare.image_save_success", Path.GetFileName(outputPath));
            _log.LogInformation(
                "Saved interactive image diff preview. RepositoryId {RepositoryId}. Path {Path}. Output {Output}",
                _repositoryId,
                _relativePath,
                outputPath);
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Failed to save interactive image diff preview. RepositoryId {RepositoryId}. Path {Path}. Output {Output}",
                _repositoryId,
                _relativePath,
                outputPath);
            ErrorMessage = Loc.T("compare.image_save_failed");
        }
    }

    public async Task RefreshInteractiveImageDiffPreviewAsync(CancellationToken ct = default)
    {
        if (!IsImagePreview || _lastImagePreview is null)
            return;

        await RenderInteractiveImagePreviewAsync(_lastImagePreview, ct);
        RebuildPreviewMetrics();
    }

    public void CleanupPreviewResources()
    {
        _previewCts?.Cancel();
        _audioPlayback.Stop();
        ClearAudioPlaybackStatus();
        ReleasePreviewResources();
    }

    private async Task QueueInitialPreviewLoadAsync(long leftVersionId, long rightVersionId, CancellationToken ct)
    {
        await Task.Yield();
        if (ct.IsCancellationRequested)
            return;

        if (_leftVersion?.FileVersionId != leftVersionId || _rightVersion?.FileVersionId != rightVersionId)
            return;

        await LoadPreviewAsync(ct);
    }

    private async Task LoadPreviewAsync(CancellationToken externalCt)
    {
        _previewCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _previewCts = cts;

        if (_leftVersion is null || _rightVersion is null)
        {
            ResetPreview(Loc.T("compare.preview.pick_both"));
            return;
        }

        if (_leftVersion.FileVersionId == _rightVersion.FileVersionId)
        {
            ResetPreview(Loc.T("compare.preview.select_two_different"));
            return;
        }

        ErrorMessage = null;
        IsPreviewLoading = true;
        PreviewSummary = Loc.F("compare.preview.building", _leftVersion.VersionName, _rightVersion.VersionName);
        PreviewKind = PendingDiffPreviewKind.None;
        ResetFullPreviewState();
        ReleasePreviewResources();
        SetLoadingState(Loc.T("compare.loading.title"), Loc.T("compare.loading.fetch"), 14);

        try
        {
            OperationResult<PendingFileDiffPreviewDto> result = await _mediator.Send(
                new GetFileVersionDiffPreviewQuery(_leftVersion.FileVersionId, _rightVersion.FileVersionId, 4000),
                cts.Token);

            if (cts.IsCancellationRequested)
                return;

            if (!result.Success || result.Value is null)
            {
                ResetPreview(UserFacingMessageLocalizer.LocalizeOrFallback(result.Error, "compare.preview.build_failed"));
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
                    SetLoadingState(Loc.T("compare.loading.title"), Loc.T("compare.loading.text"), 72);
                    PreviewRows.Clear();
                    WordPreviewRows.Clear();
                    _isWordSemanticPreview = false;

                    if (WordSemanticDiffBuilder.LooksLikeSemanticWordDiff(preview.Lines))
                    {
                        foreach (var row in WordSemanticDiffBuilder.Build(preview.Lines, preview.Hunks))
                            WordPreviewRows.Add(row);

                        _isWordSemanticPreview = WordPreviewRows.Count > 0;
                    }

                    // Fallback to regular diff rows when semantic Word layout is too noisy or empty.
                    if (!_isWordSemanticPreview)
                    {
                        foreach (var row in BuildDiffPreviewRows(preview.Lines, preview.Hunks))
                            PreviewRows.Add(row);
                    }

                    PreviewKind = PendingDiffPreviewKind.Text;
                    var previewSummary = string.IsNullOrWhiteSpace(preview.Message)
                        ? $"{preview.AddedLines} added / {preview.RemovedLines} removed"
                            + (preview.IsTruncated ? " (preview truncated)" : string.Empty)
                        : preview.Message;
                    PreviewSummary = IsNativeWordPreferredForPreview
                        ? $"{previewSummary} {Loc.T("compare.preview.use_word_native")}"
                        : previewSummary;
                    break;

                case PendingDiffPreviewKind.Binary:
                    SetLoadingState(Loc.T("compare.loading.title"), Loc.T("compare.loading.binary"), 80);
                    _lastBinarySummary = preview.BinarySummary;
                    _lastImagePreview = null;
                    _lastAudioPreview = null;
                    _lastRenderedChangedPixelCount = 0;
                    _lastRenderedChangedPixelRatio = null;
                    _lastRenderedChangedRegionCount = 0;
                    RebuildPreviewMetrics();
                    PreviewKind = PendingDiffPreviewKind.Binary;
                    PreviewSummary = string.IsNullOrWhiteSpace(preview.Message)
                        ? Loc.T("compare.preview.binary_ready")
                        : preview.Message;
                    break;

                case PendingDiffPreviewKind.Image:
                    SetLoadingState(Loc.T("compare.loading.title"), Loc.T("compare.loading.images"), 48);
                    _lastBinarySummary = preview.BinarySummary;
                    _lastImagePreview = preview.ImagePreview;
                    _lastAudioPreview = null;
                    await LoadImagePreviewAsync(preview.ImagePreview, cts.Token);
                    SetLoadingState(Loc.T("compare.loading.title"), Loc.T("compare.loading.render"), 82);
                    await RenderInteractiveImagePreviewAsync(preview.ImagePreview, cts.Token);
                    SetLoadingState(Loc.T("compare.loading.title"), Loc.T("compare.loading.finalize"), 96);
                    RebuildPreviewMetrics();
                    PreviewKind = PendingDiffPreviewKind.Image;
                    PreviewSummary = string.IsNullOrWhiteSpace(preview.Message)
                        ? Loc.T("compare.preview.image_ready")
                        : preview.Message;
                    break;

                case PendingDiffPreviewKind.Audio:
                    SetLoadingState(Loc.T("compare.loading.title"), Loc.T("compare.loading.audio"), 58);
                    _lastBinarySummary = preview.BinarySummary;
                    _lastImagePreview = null;
                    _lastAudioPreview = preview.AudioPreview;
                    await LoadAudioPreviewAsync(preview.AudioPreview, cts.Token);
                    SetLoadingState(Loc.T("compare.loading.title"), Loc.T("compare.loading.finalize"), 96);
                    RebuildPreviewMetrics();
                    PreviewKind = PendingDiffPreviewKind.Audio;
                    PreviewSummary = string.IsNullOrWhiteSpace(preview.Message)
                        ? Loc.T("compare.preview.audio_ready")
                        : preview.Message;
                    break;

                default:
                    ResetPreview(Loc.T("compare.preview.unsupported"));
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
            ResetPreview(Loc.T("compare.preview.load_failed"));
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsPreviewLoading = false;
                ClearLoadingState();
            }

            OnPropertyChanged(nameof(CanToggleFullFilePreview));
        }
    }

    private async Task LoadFullFilePreviewAsync()
    {
        if (_leftVersion is null || _rightVersion is null)
            return;

        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        IsFullFilePreviewMode = true;
        IsPreviewLoading = true;
        IsFullFilePreviewLoading = true;
        FullPreviewBeforeText = string.Empty;
        FullPreviewAfterText = string.Empty;
        FullPreviewSummary = string.Empty;
        PreviewSummary = Loc.F("compare.full_preview.loading", _leftVersion.VersionName, _rightVersion.VersionName);
        ErrorMessage = null;
        SetLoadingState(Loc.T("compare.loading.title"), Loc.T("compare.loading.full_text"), 20);

        try
        {
            var beforeTask = _mediator.Send(new GetFileVersionTextContentQuery(_leftVersion.FileVersionId, 4_000_000), cts.Token);
            var afterTask = _mediator.Send(new GetFileVersionTextContentQuery(_rightVersion.FileVersionId, 4_000_000), cts.Token);

            await Task.WhenAll(beforeTask, afterTask);
            if (cts.IsCancellationRequested)
                return;

            SetLoadingState(Loc.T("compare.loading.title"), Loc.T("compare.loading.finalize"), 88);

            var before = await beforeTask;
            var after = await afterTask;
            var summaryParts = new List<string>(2);

            if (before.Success && before.Value is not null)
            {
                FullPreviewBeforeText = before.Value.Content;
                summaryParts.Add(Loc.F(
                    "compare.full_preview.before_size",
                    FormatBytes(before.Value.SizeBytes),
                    before.Value.IsTruncated ? Loc.T("compare.full_preview.truncated_suffix") : string.Empty));
            }
            else
            {
                FullPreviewBeforeText = $"{Loc.T("compare.full_preview.before_unavailable")}.{Environment.NewLine}{Environment.NewLine}{before.Error}";
                summaryParts.Add(Loc.T("compare.full_preview.before_unavailable_short"));
            }

            if (after.Success && after.Value is not null)
            {
                FullPreviewAfterText = after.Value.Content;
                summaryParts.Add(Loc.F(
                    "compare.full_preview.after_size",
                    FormatBytes(after.Value.SizeBytes),
                    after.Value.IsTruncated ? Loc.T("compare.full_preview.truncated_suffix") : string.Empty));
            }
            else
            {
                FullPreviewAfterText = $"{Loc.T("compare.full_preview.after_unavailable")}.{Environment.NewLine}{Environment.NewLine}{after.Error}";
                summaryParts.Add(Loc.T("compare.full_preview.after_unavailable_short"));
            }

            FullPreviewSummary = string.Join(" | ", summaryParts);
            PreviewKind = PendingDiffPreviewKind.Text;
            PreviewSummary = Loc.T("compare.full_preview.ready");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to load full-file preview. LeftVersion {LeftVersion}. RightVersion {RightVersion}",
                _leftVersion.FileVersionId,
                _rightVersion.FileVersionId);

            FullPreviewBeforeText = string.Empty;
            FullPreviewAfterText = string.Empty;
            FullPreviewSummary = Loc.T("compare.full_preview.failed");
            PreviewSummary = Loc.T("compare.full_preview.load_failed");
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsPreviewLoading = false;
                IsFullFilePreviewLoading = false;
                ClearLoadingState();
            }

            OnPropertyChanged(nameof(CanToggleFullFilePreview));
        }
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
            SelectedPairSummary = Loc.T("compare.selected.select_from_list");
            return;
        }

        if (_leftVersion is null || _rightVersion is null)
        {
            var selected = _leftVersion ?? _rightVersion;
            SelectedPairSummary = selected is null
                ? Loc.T("compare.selected.select_from_list")
                : Loc.F("compare.selected.pick_second_side", selected.VersionName);
            return;
        }

        if (_leftVersion.FileVersionId == _rightVersion.FileVersionId)
        {
            SelectedPairSummary = Loc.T("compare.selected.select_two_different");
            return;
        }

        SelectedPairSummary = Loc.F(
            "compare.selected.left_right",
            _leftVersion.VersionName,
            _leftVersion.CreatedAtDisplay,
            _rightVersion.VersionName,
            _rightVersion.CreatedAtDisplay);
    }

    private async Task LoadImagePreviewAsync(PendingImageDiffPreviewDto? imagePreview, CancellationToken ct)
    {
        LeftImageCaption = Loc.T("common.before");
        RightImageCaption = Loc.T("common.after");
        OverlayImageCaption = BuildImageOverlayCaption();

        if (imagePreview is null)
            return;

        if (!string.IsNullOrWhiteSpace(imagePreview.BaselineImagePath) && File.Exists(imagePreview.BaselineImagePath))
            TrackTempFile(imagePreview.BaselineImagePath, imagePreview.IsBaselineTempFile);

        if (!string.IsNullOrWhiteSpace(imagePreview.CurrentImagePath) && File.Exists(imagePreview.CurrentImagePath))
            TrackTempFile(imagePreview.CurrentImagePath, imagePreview.IsCurrentTempFile);

        if (!string.IsNullOrWhiteSpace(imagePreview.OverlayImagePath) && File.Exists(imagePreview.OverlayImagePath))
            TrackTempFile(imagePreview.OverlayImagePath, imagePreview.IsOverlayTempFile);

        var leftImageTask = LoadBitmapAsync(imagePreview.BaselineImagePath, ct);
        var rightImageTask = LoadBitmapAsync(imagePreview.CurrentImagePath, ct);
        await Task.WhenAll(leftImageTask, rightImageTask);
        if (ct.IsCancellationRequested)
            return;

        LeftImagePreview = await leftImageTask;
        RightImagePreview = await rightImageTask;

        LeftImageCaption = BuildImageCaption(Loc.T("common.before"), imagePreview.BaselineWidth, imagePreview.BaselineHeight);
        RightImageCaption = BuildImageCaption(Loc.T("common.after"), imagePreview.CurrentWidth, imagePreview.CurrentHeight);
    }

    private async Task LoadAudioPreviewAsync(PendingAudioDiffPreviewDto? audioPreview, CancellationToken ct)
    {
        if (audioPreview is null)
            return;

        var waveformTask = LoadBitmapAsync(audioPreview.WaveformImagePath, ct);
        var spectrogramTask = LoadBitmapAsync(audioPreview.SpectrogramImagePath, ct);
        var spectralDeltaTask = LoadBitmapAsync(audioPreview.SpectralDeltaImagePath, ct);

        if (!string.IsNullOrWhiteSpace(audioPreview.WaveformImagePath) && File.Exists(audioPreview.WaveformImagePath))
            TrackTempFile(audioPreview.WaveformImagePath, audioPreview.IsWaveformTempFile);

        TrackTempFile(audioPreview.DifferenceAudioPath, audioPreview.IsDifferenceTempFile);

        if (!string.IsNullOrWhiteSpace(audioPreview.SpectrogramImagePath) && File.Exists(audioPreview.SpectrogramImagePath))
            TrackTempFile(audioPreview.SpectrogramImagePath, audioPreview.IsSpectrogramTempFile);

        if (!string.IsNullOrWhiteSpace(audioPreview.SpectralDeltaImagePath) && File.Exists(audioPreview.SpectralDeltaImagePath))
            TrackTempFile(audioPreview.SpectralDeltaImagePath, audioPreview.IsSpectralDeltaTempFile);

        if (!string.IsNullOrWhiteSpace(audioPreview.BaselineAudioPath) && File.Exists(audioPreview.BaselineAudioPath))
            TrackTempFile(audioPreview.BaselineAudioPath, audioPreview.IsBaselineTempFile);

        if (!string.IsNullOrWhiteSpace(audioPreview.CurrentAudioPath) && File.Exists(audioPreview.CurrentAudioPath))
            TrackTempFile(audioPreview.CurrentAudioPath, audioPreview.IsCurrentTempFile);

        await Task.WhenAll(waveformTask, spectrogramTask, spectralDeltaTask);
        if (ct.IsCancellationRequested)
            return;

        AudioWaveformPreview = await waveformTask;
        AudioSpectrogramPreview = await spectrogramTask;
        AudioSpectralDeltaPreview = await spectralDeltaTask;
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
            _lastRenderedOverlayPngBytes = Array.Empty<byte>();
            _lastRenderedChangedPixelCount = 0;
            _lastRenderedChangedPixelRatio = null;
            _lastRenderedChangedRegionCount = 0;
            SaveImageDiffPreviewCommand.NotifyCanExecuteChanged();
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
            _lastRenderedOverlayPngBytes = renderResult?.PngBytes ?? Array.Empty<byte>();

            _lastRenderedChangedPixelCount = renderResult?.ChangedPixelCount ?? 0;
            _lastRenderedChangedPixelRatio = renderResult?.ChangedPixelRatio;
            _lastRenderedChangedRegionCount = renderResult?.ChangedRegionCount ?? 0;
            SaveImageDiffPreviewCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Failed to render interactive image diff preview. Path {Path}. Mode {Mode}. Sensitivity {Sensitivity}",
                _relativePath,
                SelectedImageDiffMode?.Mode ?? ImageDiffVisualizationMode.Overlay,
                ImageDiffSensitivity);
            OverlayImagePreview?.Dispose();
            OverlayImagePreview = null;
            _lastRenderedOverlayPngBytes = Array.Empty<byte>();
            _lastRenderedChangedPixelCount = 0;
            _lastRenderedChangedPixelRatio = null;
            _lastRenderedChangedRegionCount = 0;
            SaveImageDiffPreviewCommand.NotifyCanExecuteChanged();
        }
    }

    private Task<InteractiveImageDiffRenderResult?> TryRenderCurrentImageDiffAsync(CancellationToken ct)
    {
        if (_lastImagePreview is null)
            return Task.FromResult<InteractiveImageDiffRenderResult?>(null);

        return InteractiveImageDiffRenderer.TryRenderAsync(
            _lastImagePreview.BaselineImagePath,
            _lastImagePreview.CurrentImagePath,
            ImageDiffSensitivity,
            SelectedImageDiffMode?.Mode ?? ImageDiffVisualizationMode.Overlay,
            ComparisonSplitPercent,
            ShowImageDiffRegionBoxes,
            Loc.T("common.before"),
            Loc.T("common.after"),
            ct);
    }

    private string BuildImageOverlayCaption()
    {
        var modeLabel = SelectedImageDiffMode?.Label ?? Loc.T("compare.image_mode.overlay");
        return $"{Loc.T("compare.overlay")} - {modeLabel}";
    }

    private void RebuildPreviewMetrics()
    {
        PreviewMetrics.Clear();
        AudioChangedSegments.Clear();

        if (_lastBinarySummary is null)
            return;

        PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
            Loc.T("metric.size"),
            $"{FormatBytes(_lastBinarySummary.BaselineSizeBytes)} -> {FormatBytes(_lastBinarySummary.CurrentSizeBytes)} ({FormatSignedBytes(_lastBinarySummary.SizeDeltaBytes)})"));

        PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
            Loc.T("metric.blocks"),
            $"{_lastBinarySummary.BaselineBlockCount} -> {_lastBinarySummary.CurrentBlockCount}, shared {_lastBinarySummary.SharedBlockCount}"));

        var dedupRatio = _lastBinarySummary.DedupRatio ?? ComputeDedupRatio(_lastBinarySummary);
        var changedRatio = _lastBinarySummary.ChangedBlockRatio ?? ComputeChangedBlockRatio(_lastBinarySummary, dedupRatio);

        PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
            Loc.T("metric.dedup_changed"),
            $"{FormatRatio(dedupRatio)} / {FormatRatio(changedRatio)}"));

        if (_lastBinarySummary.ByteSimilarityRatio.HasValue)
        {
            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.byte_similarity"),
                $"{_lastBinarySummary.ByteSimilarityRatio.Value * 100:F1}%"));
        }

        if (_lastAudioPreview is not null)
        {
            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.duration"),
                $"{FormatAudioDuration(_lastAudioPreview.BaselineDurationSeconds)} -> {FormatAudioDuration(_lastAudioPreview.CurrentDurationSeconds)}"
                + (_lastAudioPreview.HasDurationMismatch ? $" {Loc.T("metric.changed_suffix")}" : string.Empty)));

            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.sample_rate"),
                $"{FormatSampleRate(_lastAudioPreview.BaselineSampleRate)} -> {FormatSampleRate(_lastAudioPreview.CurrentSampleRate)}"));

            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.channels"),
                $"{FormatChannels(_lastAudioPreview.BaselineChannels)} -> {FormatChannels(_lastAudioPreview.CurrentChannels)}"));

            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.peak"),
                $"{FormatAmplitude(_lastAudioPreview.BaselinePeakAmplitude)} -> {FormatAmplitude(_lastAudioPreview.CurrentPeakAmplitude)}"));

            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.rms"),
                $"{FormatAmplitude(_lastAudioPreview.BaselineRmsAmplitude)} -> {FormatAmplitude(_lastAudioPreview.CurrentRmsAmplitude)}"));

            if (_lastAudioPreview.SignalSimilarityRatio.HasValue)
            {
                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.T("metric.audio_similarity"),
                    $"{_lastAudioPreview.SignalSimilarityRatio.Value * 100:F1}%"));
            }

            if (_lastAudioPreview.SpectralSimilarityRatio.HasValue)
            {
                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.T("metric.spectral_similarity"),
                    $"{_lastAudioPreview.SpectralSimilarityRatio.Value * 100:F1}%"));
            }

            if (_lastAudioPreview.SpectralDeltaRatio.HasValue)
            {
                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.T("metric.spectral_delta"),
                    $"{_lastAudioPreview.SpectralDeltaRatio.Value * 100:F1}%"));
            }

            if (_lastAudioPreview.BaselineStereoCorrelation.HasValue || _lastAudioPreview.CurrentStereoCorrelation.HasValue)
            {
                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.T("metric.stereo_correlation"),
                    $"{FormatSignedRatio(_lastAudioPreview.BaselineStereoCorrelation)} -> {FormatSignedRatio(_lastAudioPreview.CurrentStereoCorrelation)}"));
            }

            if (_lastAudioPreview.ChangedTimeRatio.HasValue)
            {
                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.T("metric.changed_timeline"),
                    $"{_lastAudioPreview.ChangedTimeRatio.Value * 100:F1}%"));
            }

            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.changed_segments"),
                $"{_lastAudioPreview.ChangedSegmentCount:N0}"));

            foreach (var bandMetric in _lastAudioPreview.BandMetrics)
            {
                var bandName = LocalizeAudioBandName(bandMetric.BandDisplayName);
                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.F("metric.band_energy", bandName),
                    $"{bandMetric.BaselineEnergyRatio * 100:F1}% -> {bandMetric.CurrentEnergyRatio * 100:F1}%"));

                PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                    Loc.F("metric.band_similarity", bandName),
                    $"{bandMetric.SimilarityRatio * 100:F1}% ({Loc.T("metric.delta_short")} {bandMetric.DeltaRatio * 100:F1}%)"));
            }

            foreach (var channelMetric in _lastAudioPreview.ChannelMetrics)
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

            foreach (var segment in _lastAudioPreview.ChangedSegments)
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

        if (_lastImagePreview is null)
            return;

        var before = _lastImagePreview.BaselineWidth is null || _lastImagePreview.BaselineHeight is null
            ? Loc.T("common.not_available_short")
            : $"{_lastImagePreview.BaselineWidth} x {_lastImagePreview.BaselineHeight}";

        var after = _lastImagePreview.CurrentWidth is null || _lastImagePreview.CurrentHeight is null
            ? Loc.T("common.not_available_short")
            : $"{_lastImagePreview.CurrentWidth} x {_lastImagePreview.CurrentHeight}";

        PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
            Loc.T("metric.dimensions"),
            $"{before} -> {after}" + (_lastImagePreview.HasDimensionMismatch ? $" {Loc.T("metric.changed_suffix")}" : string.Empty)));

        if (_lastImagePreview.SimilarityRatio.HasValue)
        {
            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.image_similarity"),
                $"{_lastImagePreview.SimilarityRatio.Value * 100:F1}%"));
        }

        if (_lastRenderedChangedPixelRatio.HasValue || _lastRenderedChangedPixelCount > 0)
        {
            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.changed_area"),
                _lastRenderedChangedPixelRatio.HasValue
                    ? $"{_lastRenderedChangedPixelRatio.Value * 100:F1}% ({_lastRenderedChangedPixelCount:N0} px)"
                    : $"{_lastRenderedChangedPixelCount:N0} px"));

            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.changed_regions"),
                $"{_lastRenderedChangedRegionCount:N0}"));
        }

        if (_lastImagePreview.IsVectorImage)
        {
            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.svg_structure"),
                $"+{_lastImagePreview.AddedElementCount:N0} / -{_lastImagePreview.RemovedElementCount:N0} / ~{_lastImagePreview.ModifiedElementCount:N0}"));

            PreviewMetrics.Add(new SnapshotPreviewMetricItemViewModel(
                Loc.T("metric.svg_attributes"),
                $"{_lastImagePreview.ChangedAttributeCount:N0}"));
        }
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
        _audioPlayback.Stop();
        ClearAudioPlaybackStatus();
        PreviewKind = PendingDiffPreviewKind.None;
        IsPreviewLoading = false;
        ClearLoadingState();
        PreviewSummary = message;
        PreviewRows.Clear();
        WordPreviewRows.Clear();
        PreviewMetrics.Clear();
        AudioChangedSegments.Clear();
        _isWordSemanticPreview = false;
        _lastBinarySummary = null;
        _lastImagePreview = null;
        _lastAudioPreview = null;
        _lastRenderedOverlayPngBytes = Array.Empty<byte>();
        _lastRenderedChangedPixelCount = 0;
        _lastRenderedChangedPixelRatio = null;
        _lastRenderedChangedRegionCount = 0;
        ResetFullPreviewState();
        OnPropertyChanged(nameof(CanToggleFullFilePreview));
        SaveImageDiffPreviewCommand.NotifyCanExecuteChanged();
    }

    private void ReleasePreviewResources()
    {
        _imageDiffRenderCts?.Cancel();
        _audioPlayback.Stop();
        ClearAudioPlaybackStatus();
        ClearLoadingState();
        PreviewRows.Clear();
        WordPreviewRows.Clear();
        PreviewMetrics.Clear();
        AudioChangedSegments.Clear();
        PreviewKind = PendingDiffPreviewKind.None;
        _isWordSemanticPreview = false;
        _lastBinarySummary = null;
        _lastImagePreview = null;
        _lastAudioPreview = null;
        _lastRenderedOverlayPngBytes = Array.Empty<byte>();
        _lastRenderedChangedPixelCount = 0;
        _lastRenderedChangedPixelRatio = null;
        _lastRenderedChangedRegionCount = 0;
        ResetFullPreviewState();

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
        OverlayImageCaption = Loc.T("compare.overlay");

        foreach (var path in _tempPreviewFiles)
            TryDelete(path);

        _tempPreviewFiles.Clear();
        SaveImageDiffPreviewCommand.NotifyCanExecuteChanged();
    }

    private void TrackTempFile(string? path, bool isTemp)
    {
        if (isTemp && !string.IsNullOrWhiteSpace(path))
            _tempPreviewFiles.Add(path);
    }

    private static Task<Bitmap?> LoadBitmapAsync(string? imagePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return Task.FromResult<Bitmap?>(null);

        return Task.Run(() => (Bitmap?)new Bitmap(imagePath), ct);
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

    private static void CleanupStaleNativeWordCompareFiles(string directoryPath)
    {
        try
        {
            var threshold = DateTime.UtcNow.AddDays(-2);
            foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                var createdAtUtc = File.GetCreationTimeUtc(file);
                if (createdAtUtc < threshold)
                    TryDelete(file);
            }
        }
        catch
        {
        }
    }

    private static string NormalizeRelativePath(string value)
        => value.Replace('\\', '/').Trim();

    private void SetLoadingState(string title, string detail, double progress, bool indeterminate = false)
    {
        LoadingTitle = title;
        LoadingDetail = detail;
        LoadingProgressValue = Math.Clamp(progress, 0, 100);
        IsLoadingProgressIndeterminate = indeterminate;
    }

    private void ClearLoadingState()
    {
        LoadingTitle = string.Empty;
        LoadingDetail = string.Empty;
        LoadingProgressValue = 0;
        IsLoadingProgressIndeterminate = false;
    }

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
        => value.HasValue ? $"{value.Value * 100:F1}%" : Loc.T("common.not_available_short");

    private static double? ComputeDedupRatio(PendingBinaryDiffSummaryDto summary)
    {
        var denominator = summary.CurrentBlockCount > 0
            ? summary.CurrentBlockCount
            : summary.BaselineBlockCount;

        if (denominator <= 0)
            return null;

        var shared = System.Math.Clamp(summary.SharedBlockCount, 0, denominator);
        return (double)shared / denominator;
    }

    private static double? ComputeChangedBlockRatio(PendingBinaryDiffSummaryDto summary, double? dedupRatio)
    {
        if (summary.CurrentBlockCount <= 0)
            return null;

        if (dedupRatio.HasValue)
            return System.Math.Clamp(1d - dedupRatio.Value, 0d, 1d);

        var unchanged = System.Math.Clamp(summary.SharedBlockCount, 0, summary.CurrentBlockCount);
        return (double)(summary.CurrentBlockCount - unchanged) / summary.CurrentBlockCount;
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
        OnPropertyChanged(nameof(IsWordRichPreview));
        OnPropertyChanged(nameof(HasNoPreviewContent));
        OnPropertyChanged(nameof(ShowDiffRowsPanel));
        OnPropertyChanged(nameof(ShowWordDiffRowsPanel));
        OnPropertyChanged(nameof(ShowNoDiffPreviewMessage));
        OnPropertyChanged(nameof(CanToggleFullFilePreview));
    }

    private void OnWordPreviewRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasWordPreviewRows));
        OnPropertyChanged(nameof(IsTextPreview));
        OnPropertyChanged(nameof(IsWordRichPreview));
        OnPropertyChanged(nameof(HasNoPreviewContent));
        OnPropertyChanged(nameof(ShowDiffRowsPanel));
        OnPropertyChanged(nameof(ShowWordDiffRowsPanel));
        OnPropertyChanged(nameof(ShowNoDiffPreviewMessage));
        OnPropertyChanged(nameof(CanToggleFullFilePreview));
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
}
