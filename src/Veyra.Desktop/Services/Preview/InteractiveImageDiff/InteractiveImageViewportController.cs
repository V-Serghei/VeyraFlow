using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Services.Preview;

internal sealed class InteractiveImageViewportController
{
    private const double MinZoomPercent = 1d;
    private const double MaxZoomPercent = 500d;
    private const double MinSplitPercent = ImageDiffPreviewDefaults.MinSplitPercent;
    private const double MaxSplitPercent = ImageDiffPreviewDefaults.MaxSplitPercent;

    private readonly ScrollViewer _scrollViewer;
    private readonly Grid _viewportHost;
    private readonly Grid _imageHost;
    private readonly Image? _overlayImage;
    private readonly Image? _splitBaseImage;
    private readonly Border? _splitRevealHost;
    private readonly Image? _splitRevealImage;
    private readonly Border? _splitDivider;
    private readonly Image? _peekBeforeImage;
    private readonly Slider? _zoomSlider;
    private readonly TextBlock? _zoomValueText;
    private readonly Func<Bitmap?> _getOverlayBitmap;
    private readonly Func<Bitmap?> _getLeftBitmap;
    private readonly Func<Bitmap?> _getRightBitmap;
    private readonly Func<Bitmap?> _getReferenceBitmap;
    private readonly Func<bool> _isSplitMode;
    private readonly Func<double> _getSplitPercent;
    private readonly Action<double>? _setSplitPercent;

    private bool _suppressZoomEvents;
    private bool _zoomInitialized;
    private bool _zoomUserAdjusted;
    private double _zoomPercent = 100d;
    private bool _isDraggingSplit;
    private bool _isShowingPeek;
    private bool _isCleaningUp;
    private bool _isSpacePressed;
    private bool _isPanning;
    private bool _panWithMiddleButton;
    private Point _lastPanPoint;
    private double _displayedSplitPercent = 50d;

    public InteractiveImageViewportController(
        ScrollViewer scrollViewer,
        Grid viewportHost,
        Grid imageHost,
        Image? overlayImage,
        Image? splitBaseImage,
        Border? splitRevealHost,
        Image? splitRevealImage,
        Border? splitDivider,
        Image? peekBeforeImage,
        Slider? zoomSlider,
        TextBlock? zoomValueText,
        Func<Bitmap?> getOverlayBitmap,
        Func<Bitmap?> getLeftBitmap,
        Func<Bitmap?> getRightBitmap,
        Func<Bitmap?> getReferenceBitmap,
        Func<bool> isSplitMode,
        Func<double> getSplitPercent,
        Action<double>? setSplitPercent)
    {
        _scrollViewer = scrollViewer;
        _viewportHost = viewportHost;
        _imageHost = imageHost;
        _overlayImage = overlayImage;
        _splitBaseImage = splitBaseImage;
        _splitRevealHost = splitRevealHost;
        _splitRevealImage = splitRevealImage;
        _splitDivider = splitDivider;
        _peekBeforeImage = peekBeforeImage;
        _zoomSlider = zoomSlider;
        _zoomValueText = zoomValueText;
        _getOverlayBitmap = getOverlayBitmap;
        _getLeftBitmap = getLeftBitmap;
        _getRightBitmap = getRightBitmap;
        _getReferenceBitmap = getReferenceBitmap;
        _isSplitMode = isSplitMode;
        _getSplitPercent = getSplitPercent;
        _setSplitPercent = setSplitPercent;
    }

    public bool IsUserZoomAdjusted => _zoomUserAdjusted;

    public void Cleanup()
    {
        _isCleaningUp = true;
        _zoomInitialized = false;
        _zoomUserAdjusted = false;
        _isDraggingSplit = false;
        _isShowingPeek = false;
        _isSpacePressed = false;
        _isPanning = false;
        _panWithMiddleButton = false;
    }

    public void UpdateZoomUi()
    {
        if (_zoomSlider is not null)
        {
            _suppressZoomEvents = true;
            try
            {
                _zoomSlider.Value = _zoomPercent;
            }
            finally
            {
                _suppressZoomEvents = false;
            }
        }

        if (_zoomValueText is not null)
            _zoomValueText.Text = $"{Math.Round(_zoomPercent):0}%";
    }

    public void HandleContentChanged()
    {
        if (_isCleaningUp)
            return;

        UpdatePresentation();
        if (!HasValidBitmap())
            return;

        if (!_zoomInitialized || !_zoomUserAdjusted)
        {
            _zoomUserAdjusted = false;
            QueueFitToView();
            return;
        }

        ApplyZoom(_zoomPercent, preserveViewport: false);
    }

    public void HandleLayoutChanged()
    {
        if (!_zoomUserAdjusted)
            QueueFitToView();
    }

    public void HandleSplitPercentChanged()
    {
        _displayedSplitPercent = _getSplitPercent();
        UpdateSplitVisual(_displayedSplitPercent);
    }

    public void HandleZoomSliderValueChanged(double value)
    {
        if (_suppressZoomEvents)
            return;

        _zoomUserAdjusted = true;
        ApplyZoom(value, preserveViewport: true);
    }

    public void HandlePointerWheel(object? source, PointerWheelEventArgs e)
    {
        if (e.Handled
            || (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            || !HasValidBitmap()
            || !IsWheelTarget(source))
        {
            return;
        }

        e.Handled = true;
        var factor = e.Delta.Y >= 0 ? 1.1d : 1d / 1.1d;
        _zoomUserAdjusted = true;
        ApplyZoom(_zoomPercent * factor, preserveViewport: true, focusPoint: e.GetPosition(_scrollViewer));
    }

    public void HandlePointerPressed(PointerPressedEventArgs e)
    {
        if (_isCleaningUp)
            return;

        _scrollViewer.Focus();
        var point = e.GetCurrentPoint(_scrollViewer);
        if (point.Properties.IsMiddleButtonPressed && HasAnyImage())
        {
            StartPanning(point.Position, withMiddleButton: true);
            e.Pointer.Capture(_scrollViewer);
            e.Handled = true;
            return;
        }

        if (_isSpacePressed && point.Properties.IsLeftButtonPressed && HasAnyImage())
        {
            StartPanning(point.Position, withMiddleButton: false);
            e.Pointer.Capture(_scrollViewer);
            e.Handled = true;
            return;
        }

        if (!_isSpacePressed && point.Properties.IsLeftButtonPressed && e.ClickCount >= 2)
        {
            ToggleQuickZoom(e.GetPosition(_scrollViewer));
            e.Handled = true;
            return;
        }

        if (point.Properties.IsRightButtonPressed && _getLeftBitmap() is not null)
        {
            _isShowingPeek = true;
            SetPeekVisible(true);
            e.Pointer.Capture(_scrollViewer);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed
            || !_isSplitMode()
            || !HasAnyImage())
        {
            return;
        }

        _isDraggingSplit = true;
        _zoomUserAdjusted = true;
        UpdateSplitFromPointer(point.Position, commitToViewModel: false);
        e.Pointer.Capture(_scrollViewer);
        e.Handled = true;
    }

    public void HandlePointerMoved(PointerEventArgs e)
    {
        if (_isPanning)
        {
            UpdatePan(e.GetPosition(_scrollViewer));
            e.Handled = true;
            return;
        }

        if (!_isDraggingSplit)
            return;

        UpdateSplitFromPointer(e.GetPosition(_scrollViewer), commitToViewModel: false);
        e.Handled = true;
    }

    public void HandlePointerReleased(PointerReleasedEventArgs e)
    {
        var released = false;

        if (_isPanning)
        {
            StopPanning();
            released = true;
        }

        if (_isDraggingSplit)
        {
            _isDraggingSplit = false;
            UpdateSplitFromPointer(e.GetPosition(_scrollViewer), commitToViewModel: true);
            released = true;
        }

        if (_isShowingPeek)
        {
            _isShowingPeek = false;
            SetPeekVisible(false);
            released = true;
        }

        if (!released)
            return;

        e.Pointer.Capture(null);
        e.Handled = true;
    }

    public void HandlePointerCaptureLost()
    {
        _isDraggingSplit = false;
        StopPanning();

        if (!_isShowingPeek)
            return;

        _isShowingPeek = false;
        SetPeekVisible(false);
    }

    public void HandleKeyDown(KeyEventArgs e)
    {
        if (_isCleaningUp || e.Key != Key.Space)
            return;

        _isSpacePressed = true;
        e.Handled = true;
    }

    public void HandleKeyUp(KeyEventArgs e)
    {
        if (e.Key != Key.Space)
            return;

        _isSpacePressed = false;
        e.Handled = true;
    }

    public void HandleFocusLost()
    {
        _isSpacePressed = false;
    }

    public void ZoomOut()
    {
        _zoomUserAdjusted = true;
        ApplyZoom(_zoomPercent / 1.1d, preserveViewport: true);
    }

    public void ZoomIn()
    {
        _zoomUserAdjusted = true;
        ApplyZoom(_zoomPercent * 1.1d, preserveViewport: true);
    }

    public void FitToView()
    {
        _zoomUserAdjusted = false;
        QueueFitToView();
    }

    public void ResetToHundred()
    {
        _zoomUserAdjusted = true;
        ApplyZoom(100d, preserveViewport: false);
    }

    private void ToggleQuickZoom(Point focusPoint)
    {
        var target = Math.Abs(_zoomPercent - 100d) < 0.5d ? FitPercent() : 100d;
        var preserveViewport = Math.Abs(target - 100d) < 0.5d;
        _zoomUserAdjusted = true;
        ApplyZoom(target, preserveViewport, focusPoint);
    }

    private double FitPercent()
    {
        var bitmap = _getReferenceBitmap();
        if (!TryGetBitmapPixelSize(bitmap, out var bitmapSize))
            return 100d;

        var viewport = _scrollViewer.Viewport;
        var availableWidth = viewport.Width > 1 ? viewport.Width : _scrollViewer.Bounds.Width;
        var availableHeight = viewport.Height > 1 ? viewport.Height : _scrollViewer.Bounds.Height;
        if (availableWidth <= 1 || availableHeight <= 1)
            return 100d;

        var scale = availableWidth / bitmapSize.Width;
        return Math.Clamp(scale * 100d, MinZoomPercent, MaxZoomPercent);
    }

    private void QueueFitToView()
    {
        if (_isCleaningUp)
            return;

        Dispatcher.UIThread.Post(FitToViewInternal, DispatcherPriority.Background);
    }

    private void FitToViewInternal()
    {
        if (_isCleaningUp)
            return;

        ApplyZoom(FitPercent(), preserveViewport: false);
        _zoomInitialized = true;
    }

    private void ApplyZoom(double percent, bool preserveViewport, Point? focusPoint = null)
    {
        if (_isCleaningUp)
            return;

        var bitmap = _getReferenceBitmap();
        if (!TryGetBitmapPixelSize(bitmap, out var bitmapSize))
            return;

        var clampedPercent = Math.Clamp(percent, MinZoomPercent, MaxZoomPercent);
        var oldWidth = _imageHost.Width > 0 ? _imageHost.Width : bitmapSize.Width;
        var oldHeight = _imageHost.Height > 0 ? _imageHost.Height : bitmapSize.Height;
        var oldOffset = _scrollViewer.Offset;
        var viewport = _scrollViewer.Viewport;
        var focus = focusPoint ?? new Point(viewport.Width / 2d, viewport.Height / 2d);
        var shouldPreserveViewport = preserveViewport && oldWidth > 0 && oldHeight > 0;

        var relativeX = oldWidth > 0 ? Math.Clamp((oldOffset.X + focus.X) / oldWidth, 0d, 1d) : 0d;
        var relativeY = oldHeight > 0 ? Math.Clamp((oldOffset.Y + focus.Y) / oldHeight, 0d, 1d) : 0d;

        var newWidth = Math.Max(1d, bitmapSize.Width * clampedPercent / 100d);
        var newHeight = Math.Max(1d, bitmapSize.Height * clampedPercent / 100d);

        ApplyVisualSize(_overlayImage, newWidth, newHeight);
        ApplyVisualSize(_splitBaseImage, newWidth, newHeight);
        ApplyVisualSize(_splitRevealImage, newWidth, newHeight);
        ApplyVisualSize(_peekBeforeImage, newWidth, newHeight);

        _imageHost.Width = newWidth;
        _imageHost.Height = newHeight;
        _imageHost.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        _imageHost.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;

        _viewportHost.Width = newWidth;
        _viewportHost.Height = newHeight;
        _viewportHost.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        _viewportHost.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;

        _zoomPercent = clampedPercent;
        _zoomInitialized = true;
        UpdateZoomUi();
        UpdateSplitVisual();

        Dispatcher.UIThread.Post(() =>
        {
            if (_isCleaningUp)
                return;

            var currentViewport = _scrollViewer.Viewport;
            var currentViewportWidth = currentViewport.Width > 1 ? currentViewport.Width : _scrollViewer.Bounds.Width;
            var currentViewportHeight = currentViewport.Height > 1 ? currentViewport.Height : _scrollViewer.Bounds.Height;
            var maxOffsetX = Math.Max(0d, newWidth - currentViewportWidth);
            var maxOffsetY = Math.Max(0d, newHeight - currentViewportHeight);
            var contentFits = newWidth <= currentViewportWidth + 0.5d && newHeight <= currentViewportHeight + 0.5d;

            if (!shouldPreserveViewport || contentFits)
            {
                _scrollViewer.Offset = new Vector(0d, 0d);
                return;
            }

            var targetX = maxOffsetX <= 0d
                ? 0d
                : Math.Clamp((newWidth * relativeX) - focus.X, 0d, maxOffsetX);
            var targetY = maxOffsetY <= 0d
                ? 0d
                : Math.Clamp((newHeight * relativeY) - focus.Y, 0d, maxOffsetY);

            _scrollViewer.Offset = new Vector(targetX, targetY);
        }, DispatcherPriority.Background);
    }

    private static void ApplyVisualSize(Layoutable? visual, double width, double height)
    {
        if (visual is null)
            return;

        visual.Width = width;
        visual.Height = height;
        visual.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        visual.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
    }

    private void UpdatePresentation()
    {
        if (_isCleaningUp)
            return;

        var overlayBitmap = _getOverlayBitmap();
        var leftBitmap = _getLeftBitmap();
        var rightBitmap = _getRightBitmap();
        var showSplitViewer = _isSplitMode() && leftBitmap is not null && rightBitmap is not null;
        var showOverlayViewer = !showSplitViewer && overlayBitmap is not null;
        var showFallbackViewer = !showSplitViewer && overlayBitmap is null;

        if (_overlayImage is not null)
        {
            _overlayImage.Source = showFallbackViewer ? rightBitmap ?? leftBitmap : overlayBitmap;
            _overlayImage.IsVisible = (showOverlayViewer || showFallbackViewer) && !_isShowingPeek;
        }

        if (_splitBaseImage is not null)
            _splitBaseImage.IsVisible = showSplitViewer && !_isShowingPeek;

        if (_splitRevealHost is not null)
            _splitRevealHost.IsVisible = showSplitViewer && !_isShowingPeek;

        if (_splitDivider is not null)
            _splitDivider.IsVisible = showSplitViewer && !_isShowingPeek;

        if (_isShowingPeek)
            SetPeekVisible(true);
        else
        {
            _displayedSplitPercent = _getSplitPercent();
            UpdateSplitVisual(_displayedSplitPercent);
        }
    }

    private void UpdateSplitVisual(double? splitPercent = null)
    {
        if (_isCleaningUp
            || !_isSplitMode()
            || _splitRevealHost is null
            || _splitDivider is null)
        {
            return;
        }

        var imageWidth = _imageHost.Width > 0 ? _imageHost.Width : _imageHost.Bounds.Width;
        var imageHeight = _imageHost.Height > 0 ? _imageHost.Height : _imageHost.Bounds.Height;
        if (imageWidth <= 1 || imageHeight <= 1)
            return;

        var effectiveSplitPercent = Math.Clamp(splitPercent ?? _getSplitPercent(), MinSplitPercent, MaxSplitPercent);
        var splitWidth = Math.Clamp(imageWidth * (effectiveSplitPercent / 100d), 0d, imageWidth);

        _splitRevealHost.Width = splitWidth;
        _splitRevealHost.Height = imageHeight;
        _splitDivider.Height = imageHeight;
        _splitDivider.Margin = new Thickness(Math.Max(0d, splitWidth - (_splitDivider.Width / 2d)), 0d, 0d, 0d);
    }

    private void UpdateSplitFromPointer(Point pointerPosition, bool commitToViewModel)
    {
        if (!_isSplitMode())
            return;

        var imageWidth = _imageHost.Bounds.Width > 1
            ? _imageHost.Bounds.Width
            : (_imageHost.Width > 1 ? _imageHost.Width : 0d);
        if (imageWidth <= 1)
            return;

        var contentX = _scrollViewer.Offset.X + pointerPosition.X;
        var nextPercent = Math.Clamp((contentX / imageWidth) * 100d, MinSplitPercent, MaxSplitPercent);

        _displayedSplitPercent = nextPercent;
        UpdateSplitVisual(_displayedSplitPercent);

        if (!commitToViewModel
            || _setSplitPercent is null
            || Math.Abs(_getSplitPercent() - nextPercent) < 0.25d)
        {
            return;
        }

        _setSplitPercent(nextPercent);
    }

    private void SetPeekVisible(bool visible)
    {
        if (_peekBeforeImage is not null)
            _peekBeforeImage.IsVisible = visible;

        if (visible)
        {
            if (_overlayImage is not null)
                _overlayImage.IsVisible = false;
            if (_splitBaseImage is not null)
                _splitBaseImage.IsVisible = false;
            if (_splitRevealHost is not null)
                _splitRevealHost.IsVisible = false;
            if (_splitDivider is not null)
                _splitDivider.IsVisible = false;
            return;
        }

        UpdatePresentation();
    }

    private void StartPanning(Point pointerPosition, bool withMiddleButton)
    {
        _isPanning = true;
        _panWithMiddleButton = withMiddleButton;
        _lastPanPoint = pointerPosition;
    }

    private void UpdatePan(Point pointerPosition)
    {
        var delta = pointerPosition - _lastPanPoint;
        if (Math.Abs(delta.X) < 0.1d && Math.Abs(delta.Y) < 0.1d)
            return;

        var targetOffset = ClampOffset(new Vector(
            _scrollViewer.Offset.X - delta.X,
            _scrollViewer.Offset.Y - delta.Y));

        _scrollViewer.Offset = targetOffset;
        _lastPanPoint = pointerPosition;
    }

    private void StopPanning()
    {
        _isPanning = false;
        _panWithMiddleButton = false;
    }

    private Vector ClampOffset(Vector offset)
    {
        var viewport = _scrollViewer.Viewport;
        var viewportWidth = viewport.Width > 1 ? viewport.Width : _scrollViewer.Bounds.Width;
        var viewportHeight = viewport.Height > 1 ? viewport.Height : _scrollViewer.Bounds.Height;
        var contentWidth = _imageHost.Width > 1 ? _imageHost.Width : _imageHost.Bounds.Width;
        var contentHeight = _imageHost.Height > 1 ? _imageHost.Height : _imageHost.Bounds.Height;
        var maxOffsetX = Math.Max(0d, contentWidth - viewportWidth);
        var maxOffsetY = Math.Max(0d, contentHeight - viewportHeight);

        return new Vector(
            Math.Clamp(offset.X, 0d, maxOffsetX),
            Math.Clamp(offset.Y, 0d, maxOffsetY));
    }

    private bool HasAnyImage()
        => _getOverlayBitmap() is not null || _getLeftBitmap() is not null || _getRightBitmap() is not null;

    private bool HasValidBitmap()
        => TryGetBitmapPixelSize(_getReferenceBitmap(), out _);

    private bool IsWheelTarget(object? source)
    {
        for (var current = source as StyledElement; current is not null; current = current.Parent as StyledElement)
        {
            if (ReferenceEquals(current, _scrollViewer)
                || ReferenceEquals(current, _viewportHost)
                || ReferenceEquals(current, _imageHost)
                || ReferenceEquals(current, _overlayImage)
                || ReferenceEquals(current, _splitBaseImage)
                || ReferenceEquals(current, _splitRevealHost)
                || ReferenceEquals(current, _splitRevealImage)
                || ReferenceEquals(current, _splitDivider)
                || ReferenceEquals(current, _peekBeforeImage))
            {
                return true;
            }
        }

        return false;
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
