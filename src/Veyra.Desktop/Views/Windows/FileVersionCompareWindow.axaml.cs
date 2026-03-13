using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Veyra.Desktop.Views;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views.Windows;

public partial class FileVersionCompareWindow : Window
{
    private const double CompactWidth = 1160;
    private const double NarrowWidth = 980;
    private const double MinOverlayZoomPercent = 10;
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
    private Image? _overlayImageControl;
    private Slider? _overlayZoomSlider;
    private TextBlock? _overlayZoomValueText;
    private FileVersionCompareWindowViewModel? _viewModel;

    public FileVersionCompareWindow()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;

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
            _overlayImageControl = this.FindControl<Image>("OverlayImageControl");
            _overlayZoomSlider = this.FindControl<Slider>("OverlayZoomSlider");
            _overlayZoomValueText = this.FindControl<TextBlock>("OverlayZoomValueText");

            if (DataContext is FileVersionCompareWindowViewModel vm)
            {
                AttachViewModel(vm);
                vm.RequestClose += OnRequestClose;
                vm.RequestSaveImageDiffPreview += OnRequestSaveImageDiffPreview;
            }

            UpdateOverlayZoomUi();
            QueueFitOverlayImageToView();
        };

        Closed += (_, _) =>
        {
            if (DataContext is FileVersionCompareWindowViewModel vm)
            {
                vm.RequestClose -= OnRequestClose;
                vm.RequestSaveImageDiffPreview -= OnRequestSaveImageDiffPreview;
                vm.CleanupPreviewResources();
            }

            if (_viewModel is not null)
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
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
        if (DataContext is not FileVersionCompareWindowViewModel vm)
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
        if (e.PropertyName != nameof(FileVersionCompareWindowViewModel.OverlayImagePreview))
            return;

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
        if ((e.KeyModifiers & KeyModifiers.Control) == 0)
            return;

        e.Handled = true;
        var factor = e.Delta.Y >= 0 ? 1.1d : 1d / 1.1d;
        var newPercent = _overlayZoomPercent * factor;
        _overlayZoomUserAdjusted = true;
        ApplyOverlayZoom(newPercent, preserveViewport: true, focusPoint: e.GetPosition(_overlayImageScrollViewer));
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

    private void QueueFitOverlayImageToView()
    {
        Dispatcher.UIThread.Post(FitOverlayImageToView, DispatcherPriority.Background);
    }

    private void FitOverlayImageToView()
    {
        if (_overlayImageScrollViewer is null || _overlayImageControl?.Source is not Bitmap bitmap)
            return;

        var viewport = _overlayImageScrollViewer.Viewport;
        var availableWidth = viewport.Width > 1 ? viewport.Width : _overlayImageScrollViewer.Bounds.Width;
        var availableHeight = viewport.Height > 1 ? viewport.Height : _overlayImageScrollViewer.Bounds.Height;
        if (availableWidth <= 1 || availableHeight <= 1)
            return;

        var bitmapWidth = bitmap.PixelSize.Width;
        var bitmapHeight = bitmap.PixelSize.Height;
        if (bitmapWidth <= 0 || bitmapHeight <= 0)
            return;

        var scale = System.Math.Min(availableWidth / bitmapWidth, availableHeight / bitmapHeight);
        var targetPercent = System.Math.Clamp(scale * 100d, MinOverlayZoomPercent, MaxOverlayZoomPercent);
        ApplyOverlayZoom(targetPercent, preserveViewport: false);

        _overlayImageScrollViewer.Offset = new Vector(0, 0);
        _overlayZoomInitialized = true;
    }

    private void ApplyOverlayZoom(double percent, bool preserveViewport, Point? focusPoint = null)
    {
        if (_overlayImageScrollViewer is null || _overlayImageControl?.Source is not Bitmap bitmap)
            return;

        var clampedPercent = System.Math.Clamp(percent, MinOverlayZoomPercent, MaxOverlayZoomPercent);
        var oldWidth = _overlayImageControl.Width > 0 ? _overlayImageControl.Width : bitmap.PixelSize.Width;
        var oldHeight = _overlayImageControl.Height > 0 ? _overlayImageControl.Height : bitmap.PixelSize.Height;
        var oldOffset = _overlayImageScrollViewer.Offset;
        var viewport = _overlayImageScrollViewer.Viewport;
        var focus = focusPoint ?? new Point(viewport.Width / 2d, viewport.Height / 2d);

        var relativeX = oldWidth > 0 ? (oldOffset.X + focus.X) / oldWidth : 0d;
        var relativeY = oldHeight > 0 ? (oldOffset.Y + focus.Y) / oldHeight : 0d;

        var newWidth = System.Math.Max(1d, bitmap.PixelSize.Width * clampedPercent / 100d);
        var newHeight = System.Math.Max(1d, bitmap.PixelSize.Height * clampedPercent / 100d);

        _overlayImageControl.Width = newWidth;
        _overlayImageControl.Height = newHeight;
        _overlayZoomPercent = clampedPercent;
        _overlayZoomInitialized = true;
        UpdateOverlayZoomUi();

        if (!preserveViewport)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_overlayImageScrollViewer is null)
                return;

            var currentViewport = _overlayImageScrollViewer.Viewport;
            var targetX = System.Math.Max(0d, System.Math.Min(newWidth - currentViewport.Width, (newWidth * relativeX) - focus.X));
            var targetY = System.Math.Max(0d, System.Math.Min(newHeight - currentViewport.Height, (newHeight * relativeY) - focus.Y));
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
}
