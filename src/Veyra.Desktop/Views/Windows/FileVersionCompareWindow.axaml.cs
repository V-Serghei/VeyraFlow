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
using Veyra.Desktop.Views;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views.Windows;

public partial class FileVersionCompareWindow : Window
{
    private const double CompactWidth = 1160;
    private const double NarrowWidth = 980;
    private const double MinOverlayZoomPercent = 1;
    private const double MaxOverlayZoomPercent = 500;
    private bool _isSyncingDiffScroll;
    private bool _suppressOverlayZoomEvents;
    private bool _overlayZoomInitialized;
    private bool _overlayZoomUserAdjusted;
    private double _overlayZoomPercent = 100;
    private ScrollViewer? _leftDiffScrollViewer;
    private ScrollViewer? _rightDiffScrollViewer;
    private ScrollViewer? _leftWordDiffScrollViewer;
    private ScrollViewer? _rightWordDiffScrollViewer;
    private ScrollViewer? _overlayImageScrollViewer;
    private Grid? _overlayImageViewportHost;
    private Grid? _overlayImageHost;
    private Image? _overlayImageControl;
    private Image? _overlaySplitBaseImageControl;
    private Border? _overlaySplitRevealHost;
    private Image? _overlaySplitRevealImageControl;
    private Border? _overlaySplitDivider;
    private Image? _overlayPeekBeforeImageControl;
    private Slider? _overlayZoomSlider;
    private TextBlock? _overlayZoomValueText;
    private FileVersionCompareWindowViewModel? _viewModel;
    private bool _isDraggingOverlaySplit;
    private bool _isShowingOverlayPeek;
    private bool _isCleaningUp;
    private double _overlayDisplayedSplitPercent = 50;

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
            _overlayImageScrollViewer = this.FindControl<ScrollViewer>("OverlayImageScrollViewer");
            _overlayImageViewportHost = this.FindControl<Grid>("OverlayImageViewportHost");
            _overlayImageHost = this.FindControl<Grid>("OverlayImageHost");
            _overlayImageControl = this.FindControl<Image>("OverlayImageControl");
            _overlaySplitBaseImageControl = this.FindControl<Image>("OverlaySplitBaseImageControl");
            _overlaySplitRevealHost = this.FindControl<Border>("OverlaySplitRevealHost");
            _overlaySplitRevealImageControl = this.FindControl<Image>("OverlaySplitRevealImageControl");
            _overlaySplitDivider = this.FindControl<Border>("OverlaySplitDivider");
            _overlayPeekBeforeImageControl = this.FindControl<Image>("OverlayPeekBeforeImageControl");
            _overlayZoomSlider = this.FindControl<Slider>("OverlayZoomSlider");
            _overlayZoomValueText = this.FindControl<TextBlock>("OverlayZoomValueText");
            RegisterOverlayWheelHandler(_overlayImageScrollViewer);
            RegisterOverlayWheelHandler(_overlayImageViewportHost);
            RegisterOverlayWheelHandler(_overlayImageHost);
            RegisterOverlayWheelHandler(_overlayImageControl);
            RegisterOverlayWheelHandler(_overlaySplitBaseImageControl);
            RegisterOverlayWheelHandler(_overlaySplitRevealHost);
            RegisterOverlayWheelHandler(_overlaySplitRevealImageControl);
            RegisterOverlayWheelHandler(_overlaySplitDivider);
            RegisterOverlayWheelHandler(_overlayPeekBeforeImageControl);

            if (DataContext is FileVersionCompareWindowViewModel vm)
            {
                AttachViewModel(vm);
                vm.RequestClose += OnRequestClose;
                vm.RequestSaveImageDiffPreview += OnRequestSaveImageDiffPreview;
            }

            UpdateOverlayZoomUi();
            UpdateOverlayPresentation();
            QueueFitOverlayImageToView();
        };

        Closed += (_, _) =>
        {
            _isCleaningUp = true;
            _overlayZoomInitialized = false;
            _overlayZoomUserAdjusted = false;
            _isDraggingOverlaySplit = false;
            _isShowingOverlayPeek = false;

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
            _overlayImageScrollViewer = null;
            _overlayImageViewportHost = null;
            _overlayImageHost = null;
            _overlayImageControl = null;
            _overlaySplitBaseImageControl = null;
            _overlaySplitRevealHost = null;
            _overlaySplitRevealImageControl = null;
            _overlaySplitDivider = null;
            _overlayPeekBeforeImageControl = null;
            _overlayZoomSlider = null;
            _overlayZoomValueText = null;
        };
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);

        if (!_overlayZoomUserAdjusted)
            QueueFitOverlayImageToView();
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

        var outputPath = file?.Path.LocalPath;
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
                UpdateOverlayPresentation();
                if (!HasValidOverlayBitmap())
                    return;

                if (!_overlayZoomInitialized || !_overlayZoomUserAdjusted)
                {
                    _overlayZoomUserAdjusted = false;
                    QueueFitOverlayImageToView();
                    return;
                }

                ApplyOverlayZoom(_overlayZoomPercent, preserveViewport: false);
                return;

            case nameof(FileVersionCompareWindowViewModel.ShowImageDiffDetails):
            case nameof(FileVersionCompareWindowViewModel.ShowImageDiffSettings):
            case nameof(FileVersionCompareWindowViewModel.ShowSourceImagePanels):
                Dispatcher.UIThread.Post(() =>
                {
                    if (_isCleaningUp || !HasValidOverlayBitmap())
                        return;

                    if (!_overlayZoomUserAdjusted)
                    {
                        QueueFitOverlayImageToView();
                        return;
                    }

                    ApplyOverlayZoom(_overlayZoomPercent, preserveViewport: false);
                }, DispatcherPriority.Background);
                return;

            case nameof(FileVersionCompareWindowViewModel.ComparisonSplitPercent):
                _overlayDisplayedSplitPercent = _viewModel.ComparisonSplitPercent;
                UpdateOverlaySplitVisual(_overlayDisplayedSplitPercent);
                return;
        }
    }

    private void RegisterOverlayWheelHandler(InputElement? element)
    {
        if (element is null)
            return;

        element.AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnOverlayImagePointerWheelChanged,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
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

    private void UpdateOverlayPresentation()
    {
        if (_isCleaningUp || GetViewModel() is not FileVersionCompareWindowViewModel vm)
            return;

        var showSplitViewer = vm.IsSplitImageDiffMode
                              && vm.LeftImagePreview is not null
                              && vm.RightImagePreview is not null;

        if (_overlayImageControl is not null)
            _overlayImageControl.IsVisible = !showSplitViewer && !_isShowingOverlayPeek && vm.OverlayImagePreview is not null;

        if (_overlaySplitBaseImageControl is not null)
            _overlaySplitBaseImageControl.IsVisible = showSplitViewer && !_isShowingOverlayPeek;

        if (_overlaySplitRevealHost is not null)
            _overlaySplitRevealHost.IsVisible = showSplitViewer && !_isShowingOverlayPeek;

        if (_overlaySplitDivider is not null)
            _overlaySplitDivider.IsVisible = showSplitViewer && !_isShowingOverlayPeek;

        if (_isShowingOverlayPeek)
            SetOverlayPeekVisible(true);
        else
        {
            _overlayDisplayedSplitPercent = vm.ComparisonSplitPercent;
            UpdateOverlaySplitVisual(_overlayDisplayedSplitPercent);
        }
    }

    private void UpdateOverlaySplitVisual(double? splitPercent = null)
    {
        if (_isCleaningUp
            || GetViewModel() is not FileVersionCompareWindowViewModel vm
            || _overlayImageHost is null
            || _overlaySplitRevealHost is null
            || _overlaySplitDivider is null
            || _overlaySplitBaseImageControl is null
            || !vm.IsSplitImageDiffMode)
        {
            return;
        }

        var imageWidth = _overlayImageHost.Width > 0 ? _overlayImageHost.Width : _overlayImageHost.Bounds.Width;
        var imageHeight = _overlayImageHost.Height > 0 ? _overlayImageHost.Height : _overlayImageHost.Bounds.Height;
        if (imageWidth <= 1 || imageHeight <= 1)
            return;

        var effectiveSplitPercent = splitPercent ?? vm.ComparisonSplitPercent;
        var splitWidth = System.Math.Clamp(imageWidth * (effectiveSplitPercent / 100d), 0d, imageWidth);
        _overlaySplitRevealHost.Width = splitWidth;
        _overlaySplitRevealHost.Height = imageHeight;
        _overlaySplitDivider.Height = imageHeight;
        _overlaySplitDivider.Margin = new Thickness(System.Math.Max(0d, splitWidth - (_overlaySplitDivider.Width / 2d)), 0, 0, 0);
    }

    private void RegisterOverlayImageStateAfterZoom()
    {
        UpdateOverlayZoomUi();
        UpdateOverlaySplitVisual();
    }

    private void OnViewModelPropertyChanged_Legacy()
    {
        if (!_overlayZoomInitialized || !_overlayZoomUserAdjusted)
        {
            _overlayZoomUserAdjusted = false;
            QueueFitOverlayImageToView();
            return;
        }

        ApplyOverlayZoom(_overlayZoomPercent, preserveViewport: false);
    }

    private void OnOverlayZoomSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressOverlayZoomEvents)
            return;

        _overlayZoomUserAdjusted = true;
        ApplyOverlayZoom(e.NewValue, preserveViewport: true);
    }

    private void OnOverlayImagePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled)
            return;

        if (_overlayImageScrollViewer is null
            || (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            || !HasValidOverlayBitmap()
            || !IsOverlayWheelTarget(e.Source))
            return;

        e.Handled = true;
        var factor = e.Delta.Y >= 0 ? 1.1d : 1d / 1.1d;
        var newPercent = _overlayZoomPercent * factor;
        _overlayZoomUserAdjusted = true;
        ApplyOverlayZoom(newPercent, preserveViewport: true, focusPoint: e.GetPosition(_overlayImageScrollViewer));
    }

    private bool IsOverlayWheelTarget(object? source)
    {
        for (var current = source as StyledElement; current is not null; current = current.Parent as StyledElement)
        {
            if (ReferenceEquals(current, _overlayImageScrollViewer)
                || ReferenceEquals(current, _overlayImageViewportHost)
                || ReferenceEquals(current, _overlayImageHost)
                || ReferenceEquals(current, _overlayImageControl)
                || ReferenceEquals(current, _overlaySplitBaseImageControl)
                || ReferenceEquals(current, _overlaySplitRevealHost)
                || ReferenceEquals(current, _overlaySplitRevealImageControl)
                || ReferenceEquals(current, _overlaySplitDivider)
                || ReferenceEquals(current, _overlayPeekBeforeImageControl))
            {
                return true;
            }
        }

        return false;
    }

    private void OnOverlayImageViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_overlayZoomUserAdjusted)
            QueueFitOverlayImageToView();
    }

    private void OnFitOverlayImageToViewClicked(object? sender, RoutedEventArgs e)
    {
        _overlayZoomUserAdjusted = false;
        QueueFitOverlayImageToView();
    }

    private void OnResetOverlayImageToHundredClicked(object? sender, RoutedEventArgs e)
    {
        _overlayZoomUserAdjusted = true;
        ApplyOverlayZoom(100d, preserveViewport: false);
    }

    private void OnZoomOutOverlayImageClicked(object? sender, RoutedEventArgs e)
    {
        _overlayZoomUserAdjusted = true;
        ApplyOverlayZoom(_overlayZoomPercent / 1.1d, preserveViewport: true);
    }

    private void OnZoomInOverlayImageClicked(object? sender, RoutedEventArgs e)
    {
        _overlayZoomUserAdjusted = true;
        ApplyOverlayZoom(_overlayZoomPercent * 1.1d, preserveViewport: true);
    }

    private void OnOverlayImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_overlayImageScrollViewer is null || GetViewModel() is not FileVersionCompareWindowViewModel vm)
            return;

        var point = e.GetCurrentPoint(_overlayImageScrollViewer);

        if (point.Properties.IsRightButtonPressed && vm.LeftImagePreview is not null)
        {
            _isShowingOverlayPeek = true;
            SetOverlayPeekVisible(true);
            e.Pointer.Capture(_overlayImageScrollViewer);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed || !vm.IsSplitImageDiffMode || !vm.HasAnyImagePreview)
            return;

        _isDraggingOverlaySplit = true;
        _overlayZoomUserAdjusted = true;
        UpdateOverlaySplitFromPointer(point.Position, commitToViewModel: false);
        e.Pointer.Capture(_overlayImageScrollViewer);
        e.Handled = true;
    }

    private void OnOverlayImagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingOverlaySplit || _overlayImageScrollViewer is null)
            return;

        UpdateOverlaySplitFromPointer(e.GetPosition(_overlayImageScrollViewer), commitToViewModel: false);
        e.Handled = true;
    }

    private void OnOverlayImagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_overlayImageScrollViewer is null)
            return;

        var released = false;

        if (_isDraggingOverlaySplit)
        {
            _isDraggingOverlaySplit = false;
            UpdateOverlaySplitFromPointer(e.GetPosition(_overlayImageScrollViewer), commitToViewModel: true);
            released = true;
        }

        if (_isShowingOverlayPeek)
        {
            _isShowingOverlayPeek = false;
            SetOverlayPeekVisible(false);
            released = true;
        }

        if (released)
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnOverlayImagePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _isDraggingOverlaySplit = false;

        if (!_isShowingOverlayPeek)
            return;

        _isShowingOverlayPeek = false;
        SetOverlayPeekVisible(false);
    }

    private void QueueFitOverlayImageToView()
    {
        if (_isCleaningUp)
            return;

        Dispatcher.UIThread.Post(FitOverlayImageToView, DispatcherPriority.Background);
    }

    private void FitOverlayImageToView()
    {
        if (_isCleaningUp)
            return;

        var bitmap = GetOverlayReferenceBitmap();
        if (_overlayImageScrollViewer is null || !TryGetBitmapPixelSize(bitmap, out var bitmapSize))
            return;

        var viewport = _overlayImageScrollViewer.Viewport;
        var availableWidth = viewport.Width > 1 ? viewport.Width : _overlayImageScrollViewer.Bounds.Width;
        var availableHeight = viewport.Height > 1 ? viewport.Height : _overlayImageScrollViewer.Bounds.Height;
        if (availableWidth <= 1 || availableHeight <= 1)
            return;

        var bitmapWidth = bitmapSize.Width;
        var bitmapHeight = bitmapSize.Height;
        if (bitmapWidth <= 0 || bitmapHeight <= 0)
            return;

        var scale = System.Math.Min(availableWidth / bitmapWidth, availableHeight / bitmapHeight);
        var targetPercent = System.Math.Clamp(scale * 100d, MinOverlayZoomPercent, MaxOverlayZoomPercent);
        ApplyOverlayZoom(targetPercent, preserveViewport: false);
        _overlayZoomInitialized = true;
    }

    private void ApplyOverlayZoom(double percent, bool preserveViewport, Point? focusPoint = null)
    {
        if (_isCleaningUp)
            return;

        var bitmap = GetOverlayReferenceBitmap();
        if (_overlayImageScrollViewer is null
            || _overlayImageHost is null
            || _overlayImageViewportHost is null
            || !TryGetBitmapPixelSize(bitmap, out var bitmapSize))
            return;

        var clampedPercent = System.Math.Clamp(percent, MinOverlayZoomPercent, MaxOverlayZoomPercent);
        var zoomingOut = clampedPercent < (_overlayZoomPercent - 0.01d);
        var shouldPreserveViewport = preserveViewport && !zoomingOut;
        var oldWidth = _overlayImageHost?.Width > 0 ? _overlayImageHost.Width : bitmapSize.Width;
        var oldHeight = _overlayImageHost?.Height > 0 ? _overlayImageHost.Height : bitmapSize.Height;
        var oldOffset = _overlayImageScrollViewer.Offset;
        var viewport = _overlayImageScrollViewer.Viewport;
        var focus = focusPoint ?? new Point(viewport.Width / 2d, viewport.Height / 2d);

        var relativeX = oldWidth > 0 ? System.Math.Clamp((oldOffset.X + focus.X) / oldWidth, 0d, 1d) : 0d;
        var relativeY = oldHeight > 0 ? System.Math.Clamp((oldOffset.Y + focus.Y) / oldHeight, 0d, 1d) : 0d;

        var newWidth = System.Math.Max(1d, bitmapSize.Width * clampedPercent / 100d);
        var newHeight = System.Math.Max(1d, bitmapSize.Height * clampedPercent / 100d);

        if (_overlayImageControl is not null)
        {
            _overlayImageControl.Width = newWidth;
            _overlayImageControl.Height = newHeight;
        }

        if (_overlaySplitBaseImageControl is not null)
        {
            _overlaySplitBaseImageControl.Width = newWidth;
            _overlaySplitBaseImageControl.Height = newHeight;
        }

        if (_overlaySplitRevealImageControl is not null)
        {
            _overlaySplitRevealImageControl.Width = newWidth;
            _overlaySplitRevealImageControl.Height = newHeight;
        }

        if (_overlayPeekBeforeImageControl is not null)
        {
            _overlayPeekBeforeImageControl.Width = newWidth;
            _overlayPeekBeforeImageControl.Height = newHeight;
        }
        if (_overlayImageHost is not null)
        {
            _overlayImageHost.Width = newWidth;
            _overlayImageHost.Height = newHeight;
            _overlayImageHost.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            _overlayImageHost.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        }

        if (_overlayImageViewportHost is not null)
        {
            _overlayImageViewportHost.Width = newWidth;
            _overlayImageViewportHost.Height = newHeight;
            _overlayImageViewportHost.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            _overlayImageViewportHost.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        }

        _overlayZoomPercent = clampedPercent;
        _overlayZoomInitialized = true;
        RegisterOverlayImageStateAfterZoom();

        Dispatcher.UIThread.Post(() =>
        {
            if (_isCleaningUp || _overlayImageScrollViewer is null)
                return;

            var currentViewport = _overlayImageScrollViewer.Viewport;
            var currentViewportWidth = currentViewport.Width > 1 ? currentViewport.Width : _overlayImageScrollViewer.Bounds.Width;
            var currentViewportHeight = currentViewport.Height > 1 ? currentViewport.Height : _overlayImageScrollViewer.Bounds.Height;
            var maxOffsetX = System.Math.Max(0d, newWidth - currentViewportWidth);
            var maxOffsetY = System.Math.Max(0d, newHeight - currentViewportHeight);

            if (!shouldPreserveViewport)
            {
                _overlayImageScrollViewer.Offset = new Vector(0d, 0d);
                return;
            }

            var targetX = maxOffsetX <= 0d || newWidth <= currentViewportWidth + 0.5d
                ? 0d
                : System.Math.Clamp((newWidth * relativeX) - focus.X, 0d, maxOffsetX);
            var targetY = maxOffsetY <= 0d || newHeight <= currentViewportHeight + 0.5d
                ? 0d
                : System.Math.Clamp((newHeight * relativeY) - focus.Y, 0d, maxOffsetY);
            _overlayImageScrollViewer.Offset = new Vector(targetX, targetY);
        }, DispatcherPriority.Background);
    }

    private void UpdateOverlayZoomUi()
    {
        if (_overlayZoomSlider is not null)
        {
            _suppressOverlayZoomEvents = true;
            try
            {
                _overlayZoomSlider.Value = _overlayZoomPercent;
            }
            finally
            {
                _suppressOverlayZoomEvents = false;
            }
        }

        if (_overlayZoomValueText is not null)
            _overlayZoomValueText.Text = $"{System.Math.Round(_overlayZoomPercent):0}%";
    }

    private void UpdateOverlaySplitFromPointer(Point pointerPosition, bool commitToViewModel)
    {
        if (_overlayImageScrollViewer is null
            || _overlayImageViewportHost is null
            || _overlayImageHost is null
            || GetViewModel() is not FileVersionCompareWindowViewModel vm
            || !vm.IsSplitImageDiffMode)
        {
            return;
        }

        var imageWidth = _overlayImageHost.Bounds.Width > 1
            ? _overlayImageHost.Bounds.Width
            : (_overlayImageHost.Width > 1 ? _overlayImageHost.Width : 0d);
        if (imageWidth <= 1)
            return;

        var canvasWidth = _overlayImageViewportHost.Bounds.Width > 1
            ? _overlayImageViewportHost.Bounds.Width
            : (_overlayImageViewportHost.Width > 1 ? _overlayImageViewportHost.Width : imageWidth);
        var contentX = _overlayImageScrollViewer.Offset.X + pointerPosition.X;
        var nextPercent = System.Math.Clamp((contentX / imageWidth) * 100d, 0d, 100d);

        _overlayDisplayedSplitPercent = nextPercent;
        UpdateOverlaySplitVisual(_overlayDisplayedSplitPercent);

        if (!commitToViewModel || System.Math.Abs(vm.ComparisonSplitPercent - nextPercent) < 0.25d)
            return;

        vm.ComparisonSplitPercent = nextPercent;
    }

    private bool HasValidOverlayBitmap()
        => TryGetBitmapPixelSize(GetOverlayReferenceBitmap(), out _);

    private FileVersionCompareWindowViewModel? GetViewModel()
        => _viewModel ?? DataContext as FileVersionCompareWindowViewModel;

    private void SetOverlayPeekVisible(bool visible)
    {
        if (_overlayPeekBeforeImageControl is null)
            return;

        _overlayPeekBeforeImageControl.IsVisible = visible;

        if (visible)
        {
            if (_overlayImageControl is not null)
                _overlayImageControl.IsVisible = false;
            if (_overlaySplitBaseImageControl is not null)
                _overlaySplitBaseImageControl.IsVisible = false;
            if (_overlaySplitRevealHost is not null)
                _overlaySplitRevealHost.IsVisible = false;
            if (_overlaySplitDivider is not null)
                _overlaySplitDivider.IsVisible = false;
            return;
        }

        UpdateOverlayPresentation();
    }

    private static bool TryGetBitmapPixelSize(Bitmap? bitmap, out PixelSize pixelSize)
    {
        pixelSize = default;
        if (bitmap is null)
            return false;

        try
        {
            pixelSize = bitmap.PixelSize;
            return pixelSize.Width > 0 && pixelSize.Height > 0;
        }
        catch
        {
            return false;
        }
    }
}
