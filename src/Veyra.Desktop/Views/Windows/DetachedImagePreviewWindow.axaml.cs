using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Veyra.Desktop.Services.Preview;
using Veyra.Desktop.Views;

namespace Veyra.Desktop.Views.Windows;

public partial class DetachedImagePreviewWindow : Window
{
    private readonly DetachedImagePreviewRequest _request;
    private Bitmap? _overlayBitmap;
    private Bitmap? _leftBitmap;
    private Bitmap? _rightBitmap;
    private InteractiveImageViewportController? _viewportController;
    private double _splitPercent;

    public DetachedImagePreviewWindow()
    {
        InitializeComponent();
        _request = new DetachedImagePreviewRequest();
    }

    public DetachedImagePreviewWindow(DetachedImagePreviewRequest request, Window owner)
        : this()
    {
        _request = request;
        _splitPercent = request.InitialSplitPercent;
        Owner = owner;
        Title = request.Title;
        PreviewTitleTextBlock.Text = request.Title;
        AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnImagePointerWheelChanged,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        LoadBitmaps();

        if (_overlayBitmap is not null)
            PreviewImage.Source = _overlayBitmap;

        if (_rightBitmap is not null)
            SplitBaseImage.Source = _rightBitmap;

        if (_leftBitmap is not null)
        {
            SplitRevealImage.Source = _leftBitmap;
            PeekBeforeImage.Source = _leftBitmap;
        }

        Opened += (_, _) =>
        {
            WindowLayoutHelper.FitToWorkingArea(
                this,
                Owner as Window,
                maximizeToWorkingArea: false,
                frameMarginDip: 8d,
                minWidthDip: 680d,
                minHeightDip: 480d);

            _viewportController = new InteractiveImageViewportController(
                ImageScrollViewer,
                ImageViewportHost,
                ImageHost,
                PreviewImage,
                SplitBaseImage,
                SplitRevealHost,
                SplitRevealImage,
                SplitDivider,
                PeekBeforeImage,
                ZoomSlider,
                ZoomValueTextBlock,
                () => _overlayBitmap,
                () => _leftBitmap,
                () => _rightBitmap,
                GetReferenceBitmap,
                () => _request.IsSplitMode,
                () => _splitPercent,
                value => _splitPercent = value);

            _viewportController.UpdateZoomUi();
            Dispatcher.UIThread.Post(() => _viewportController?.HandleContentChanged(), DispatcherPriority.Background);
        };

        Closed += (_, _) =>
        {
            _viewportController?.Cleanup();
            _viewportController = null;
            PreviewImage.Source = null;
            SplitBaseImage.Source = null;
            SplitRevealImage.Source = null;
            PeekBeforeImage.Source = null;
            _overlayBitmap?.Dispose();
            _leftBitmap?.Dispose();
            _rightBitmap?.Dispose();
            _overlayBitmap = null;
            _leftBitmap = null;
            _rightBitmap = null;
        };
    }

    public DetachedImagePreviewWindow(byte[] pngBytes, string title, Window owner)
        : this(
            new DetachedImagePreviewRequest
            {
                Title = title,
                OverlayPngBytes = pngBytes
            },
            owner)
    {
    }

    private void LoadBitmaps()
    {
        if (_request.OverlayPngBytes.Length > 0)
        {
            using var stream = new MemoryStream(_request.OverlayPngBytes, writable: false);
            _overlayBitmap = new Bitmap(stream);
        }

        _leftBitmap = LoadBitmap(_request.LeftImagePath);
        _rightBitmap = LoadBitmap(_request.RightImagePath);
    }

    private static Bitmap? LoadBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        using var stream = File.OpenRead(path);
        return new Bitmap(stream);
    }

    private Bitmap? GetReferenceBitmap()
    {
        if (_request.IsSplitMode)
            return _leftBitmap ?? _rightBitmap ?? _overlayBitmap;

        return _overlayBitmap ?? _rightBitmap ?? _leftBitmap;
    }

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e)
        => _viewportController?.ZoomOut();

    private void OnZoomInClicked(object? sender, RoutedEventArgs e)
        => _viewportController?.ZoomIn();

    private void OnFitToWindowClicked(object? sender, RoutedEventArgs e)
        => _viewportController?.FitToView();

    private void OnResetToHundredClicked(object? sender, RoutedEventArgs e)
        => _viewportController?.ResetToHundred();

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
        => Close();

    private void OnZoomSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        => _viewportController?.HandleZoomSliderValueChanged(e.NewValue);

    private void OnImagePointerWheelChanged(object? sender, PointerWheelEventArgs e)
        => _viewportController?.HandlePointerWheel(e.Source, e);

    private void OnImagePointerPressed(object? sender, PointerPressedEventArgs e)
        => _viewportController?.HandlePointerPressed(e);

    private void OnImagePointerMoved(object? sender, PointerEventArgs e)
        => _viewportController?.HandlePointerMoved(e);

    private void OnImagePointerReleased(object? sender, PointerReleasedEventArgs e)
        => _viewportController?.HandlePointerReleased(e);

    private void OnImagePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => _viewportController?.HandlePointerCaptureLost();

    private void OnImageKeyDown(object? sender, KeyEventArgs e)
        => _viewportController?.HandleKeyDown(e);

    private void OnImageKeyUp(object? sender, KeyEventArgs e)
        => _viewportController?.HandleKeyUp(e);

    private void OnImageLostFocus(object? sender, RoutedEventArgs e)
        => _viewportController?.HandleFocusLost();

    private void OnImageViewportSizeChanged(object? sender, SizeChangedEventArgs e)
        => _viewportController?.HandleLayoutChanged();
}
