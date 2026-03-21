using System;
using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Preview;
using Veyra.Desktop.Services.Storage;
using Veyra.Desktop.Views;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views.Windows;

public partial class FileVersionCompareWindow : Window
{
    private const double CompactWidth = 1160;
    private const double NarrowWidth = 980;
    private bool _isSyncingDiffScroll;
    private ScrollViewer? _leftDiffScrollViewer;
    private ScrollViewer? _rightDiffScrollViewer;
    private ScrollViewer? _leftWordDiffScrollViewer;
    private ScrollViewer? _rightWordDiffScrollViewer;
    private InteractiveImageViewportController? _overlayViewportController;
    private FileVersionCompareWindowViewModel? _viewModel;
    private bool _isCleaningUp;

    public FileVersionCompareWindow()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnOverlayImagePointerWheelChanged,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        Opened += (_, _) =>
        {
            var ownerWindow = Owner as Window;

            WindowLayoutHelper.FitToWorkingArea(
                this,
                ownerWindow,
                maximizeToWorkingArea: false,
                frameMarginDip: 8d,
                minWidthDip: 820d,
                minHeightDip: 560d);

            Dispatcher.UIThread.Post(() =>
                WindowLayoutHelper.FitToWorkingArea(
                    this,
                    ownerWindow,
                    maximizeToWorkingArea: false,
                    frameMarginDip: 8d,
                    minWidthDip: 820d,
                    minHeightDip: 560d),
                DispatcherPriority.Background);

            ApplyResponsiveLayout(Bounds.Width);

            _leftDiffScrollViewer = this.FindControl<ScrollViewer>("LeftDiffScrollViewer");
            _rightDiffScrollViewer = this.FindControl<ScrollViewer>("RightDiffScrollViewer");
            _leftWordDiffScrollViewer = this.FindControl<ScrollViewer>("LeftWordDiffScrollViewer");
            _rightWordDiffScrollViewer = this.FindControl<ScrollViewer>("RightWordDiffScrollViewer");
            var overlayImageScrollViewer = this.FindControl<ScrollViewer>("OverlayImageScrollViewer");
            var overlayImageViewportHost = this.FindControl<Grid>("OverlayImageViewportHost");
            var overlayImageHost = this.FindControl<Grid>("OverlayImageHost");
            var overlayImageControl = this.FindControl<Image>("OverlayImageControl");
            var overlaySplitBaseImageControl = this.FindControl<Image>("OverlaySplitBaseImageControl");
            var overlaySplitRevealHost = this.FindControl<Border>("OverlaySplitRevealHost");
            var overlaySplitRevealImageControl = this.FindControl<Image>("OverlaySplitRevealImageControl");
            var overlaySplitDivider = this.FindControl<Border>("OverlaySplitDivider");
            var overlayPeekBeforeImageControl = this.FindControl<Image>("OverlayPeekBeforeImageControl");
            var overlayZoomSlider = this.FindControl<Slider>("OverlayZoomSlider");
            var overlayZoomValueText = this.FindControl<TextBlock>("OverlayZoomValueText");

            if (overlayImageScrollViewer is not null
                && overlayImageViewportHost is not null
                && overlayImageHost is not null)
            {
                _overlayViewportController = new InteractiveImageViewportController(
                    overlayImageScrollViewer,
                    overlayImageViewportHost,
                    overlayImageHost,
                    overlayImageControl,
                    overlaySplitBaseImageControl,
                    overlaySplitRevealHost,
                    overlaySplitRevealImageControl,
                    overlaySplitDivider,
                    overlayPeekBeforeImageControl,
                    overlayZoomSlider,
                    overlayZoomValueText,
                    () => GetViewModel()?.OverlayImagePreview,
                    () => GetViewModel()?.LeftImagePreview,
                    () => GetViewModel()?.RightImagePreview,
                    GetOverlayReferenceBitmap,
                    () => GetViewModel()?.IsSplitImageDiffMode ?? false,
                    () => GetViewModel()?.ComparisonSplitPercent ?? 50d,
                    value =>
                    {
                        if (GetViewModel() is { } vm)
                            vm.ComparisonSplitPercent = value;
                    });
            }

            if (DataContext is FileVersionCompareWindowViewModel vm)
            {
                AttachViewModel(vm);
                vm.RequestClose += OnRequestClose;
                vm.RequestSaveImageDiffPreview += OnRequestSaveImageDiffPreview;
            }

            _overlayViewportController?.UpdateZoomUi();
            _overlayViewportController?.HandleContentChanged();
        };

        Closed += (_, _) =>
        {
            _isCleaningUp = true;
            _overlayViewportController?.Cleanup();

            var vm = GetViewModel();
            if (vm is not null)
                vm.PropertyChanged -= OnViewModelPropertyChanged;

            if (vm is not null)
            {
                vm.RequestClose -= OnRequestClose;
                vm.RequestSaveImageDiffPreview -= OnRequestSaveImageDiffPreview;
            }

            _viewModel = null;
            DataContext = null;

            vm?.CleanupPreviewResources();

            _leftDiffScrollViewer = null;
            _rightDiffScrollViewer = null;
            _leftWordDiffScrollViewer = null;
            _rightWordDiffScrollViewer = null;
            _overlayViewportController = null;
        };
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
        _overlayViewportController?.HandleLayoutChanged();
    }

    private void ApplyResponsiveLayout(double width)
    {
        ToggleRootClass("compact-layout", width < CompactWidth);
        ToggleRootClass("narrow-layout", width < NarrowWidth);
    }

    private void ToggleRootClass(string className, bool enabled)
    {
        if (enabled)
        {
            if (!Classes.Contains(className))
                Classes.Add(className);

            return;
        }

        if (Classes.Contains(className))
            Classes.Remove(className);
    }

    private void OnRequestClose() => Close();

    private async void OnRequestSaveImageDiffPreview()
    {
        if (GetViewModel() is not FileVersionCompareWindowViewModel vm)
            return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save diff preview",
            SuggestedFileName = vm.BuildSuggestedImageDiffFileName(),
            DefaultExtension = "png",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("PNG image")
                {
                    Patterns = ["*.png"]
                }
            ]
        });

        var outputPath = StoragePathResolver.TryGetLocalPath(file);
        if (string.IsNullOrWhiteSpace(outputPath))
            return;

        await vm.SaveCurrentImageDiffPreviewAsync(outputPath);
    }

    private void OnOpenDetachedImagePreviewClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FileVersionCompareWindowViewModel vm)
            return;

        var pngBytes = vm.GetCurrentImageDiffPreviewPngBytes();
        if (pngBytes.Length == 0)
            return;

        var previewTitle = string.IsNullOrWhiteSpace(vm.OverlayImageCaption)
            ? Loc.T("compare.window_title")
            : vm.OverlayImageCaption;

        var detachedWindow = new DetachedImagePreviewWindow(
            pngBytes,
            previewTitle,
            Owner as Window ?? this)
        {
            Topmost = true
        };

        detachedWindow.Show();
    }

    private void OnVersionItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control
            || control.DataContext is not FileVersionCompareListItemViewModel item
            || DataContext is not FileVersionCompareWindowViewModel vm)
        {
            return;
        }

        var point = e.GetCurrentPoint(control);
        var selectRightSide = point.Properties.IsRightButtonPressed;

        vm.SelectVersion(item, selectRightSide);
        e.Handled = true;
    }

    private void OnLeftDiffScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer source)
            return;

        var offset = source.GetValue(ScrollViewer.OffsetProperty);
        SyncDiffScroll(_rightDiffScrollViewer, offset);
    }

    private void OnRightDiffScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer source)
            return;

        var offset = source.GetValue(ScrollViewer.OffsetProperty);
        SyncDiffScroll(_leftDiffScrollViewer, offset);
    }

    private void OnLeftWordDiffScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer source)
            return;

        var offset = source.GetValue(ScrollViewer.OffsetProperty);
        SyncDiffScroll(_rightWordDiffScrollViewer, offset);
    }

    private void OnRightWordDiffScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer source)
            return;

        var offset = source.GetValue(ScrollViewer.OffsetProperty);
        SyncDiffScroll(_leftWordDiffScrollViewer, offset);
    }

    private void SyncDiffScroll(ScrollViewer? target, Vector offset)
    {
        if (_isSyncingDiffScroll)
            return;

        if (target is null)
            return;

        var targetOffset = target.GetValue(ScrollViewer.OffsetProperty);

        if (AreClose(targetOffset.X, offset.X) && AreClose(targetOffset.Y, offset.Y))
            return;

        _isSyncingDiffScroll = true;
        try
        {
            target.SetCurrentValue(ScrollViewer.OffsetProperty, offset);
        }
        finally
        {
            _isSyncingDiffScroll = false;
        }
    }

    private static bool AreClose(double left, double right)
        => System.Math.Abs(left - right) < 0.5d;

    private void AttachViewModel(FileVersionCompareWindowViewModel vm)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = vm;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isCleaningUp || _viewModel is null || !ReferenceEquals(sender, _viewModel))
            return;

        switch (e.PropertyName)
        {
            case nameof(FileVersionCompareWindowViewModel.OverlayImagePreview):
            case nameof(FileVersionCompareWindowViewModel.LeftImagePreview):
            case nameof(FileVersionCompareWindowViewModel.RightImagePreview):
            case nameof(FileVersionCompareWindowViewModel.SelectedImageDiffMode):
                _overlayViewportController?.HandleContentChanged();
                return;

            case nameof(FileVersionCompareWindowViewModel.ShowImageDiffDetails):
            case nameof(FileVersionCompareWindowViewModel.ShowImageDiffSettings):
            case nameof(FileVersionCompareWindowViewModel.ShowSourceImagePanels):
                Dispatcher.UIThread.Post(() => _overlayViewportController?.HandleContentChanged(), DispatcherPriority.Background);
                return;

            case nameof(FileVersionCompareWindowViewModel.ComparisonSplitPercent):
                _overlayViewportController?.HandleSplitPercentChanged();
                return;
        }
    }

    private Bitmap? GetOverlayReferenceBitmap()
    {
        if (_isCleaningUp)
            return null;

        if (GetViewModel() is not FileVersionCompareWindowViewModel vm)
            return null;

        if (vm.IsSplitImageDiffMode)
            return vm.LeftImagePreview ?? vm.RightImagePreview ?? vm.OverlayImagePreview;

        return vm.OverlayImagePreview ?? vm.RightImagePreview ?? vm.LeftImagePreview;
    }

    private void OnViewModelPropertyChanged_Legacy()
    {
    }

    private void OnOverlayZoomSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        => _overlayViewportController?.HandleZoomSliderValueChanged(e.NewValue);

    private void OnOverlayImagePointerWheelChanged(object? sender, PointerWheelEventArgs e)
        => _overlayViewportController?.HandlePointerWheel(e.Source, e);

    private void OnOverlayImageViewportSizeChanged(object? sender, SizeChangedEventArgs e)
        => _overlayViewportController?.HandleLayoutChanged();

    private void OnFitOverlayImageToViewClicked(object? sender, RoutedEventArgs e)
        => _overlayViewportController?.FitToView();

    private void OnResetOverlayImageToHundredClicked(object? sender, RoutedEventArgs e)
        => _overlayViewportController?.ResetToHundred();

    private void OnZoomOutOverlayImageClicked(object? sender, RoutedEventArgs e)
        => _overlayViewportController?.ZoomOut();

    private void OnZoomInOverlayImageClicked(object? sender, RoutedEventArgs e)
        => _overlayViewportController?.ZoomIn();

    private void OnOverlayImagePointerPressed(object? sender, PointerPressedEventArgs e)
        => _overlayViewportController?.HandlePointerPressed(e);

    private void OnOverlayImagePointerMoved(object? sender, PointerEventArgs e)
        => _overlayViewportController?.HandlePointerMoved(e);

    private void OnOverlayImagePointerReleased(object? sender, PointerReleasedEventArgs e)
        => _overlayViewportController?.HandlePointerReleased(e);

    private void OnOverlayImagePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => _overlayViewportController?.HandlePointerCaptureLost();

    private void OnOverlayImageKeyDown(object? sender, KeyEventArgs e)
        => _overlayViewportController?.HandleKeyDown(e);

    private void OnOverlayImageKeyUp(object? sender, KeyEventArgs e)
        => _overlayViewportController?.HandleKeyUp(e);

    private void OnOverlayImageLostFocus(object? sender, RoutedEventArgs e)
        => _overlayViewportController?.HandleFocusLost();

    private FileVersionCompareWindowViewModel? GetViewModel()
        => _viewModel ?? DataContext as FileVersionCompareWindowViewModel;

}
