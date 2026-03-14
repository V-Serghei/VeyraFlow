using System;
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
    private readonly Bitmap? _bitmap;
    private InteractiveImageViewportController? _viewportController;

    public DetachedImagePreviewWindow()
    {
        InitializeComponent();
    }

    public DetachedImagePreviewWindow(byte[] pngBytes, string title, Window owner)
        : this()
    {
        Owner = owner;
        Title = title;
        PreviewTitleTextBlock.Text = title;
        AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnImagePointerWheelChanged,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        if (pngBytes.Length > 0)
        {
            using var stream = new MemoryStream(pngBytes, writable: false);
            _bitmap = new Bitmap(stream);
            PreviewImage.Source = _bitmap;
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
                splitBaseImage: null,
                splitRevealHost: null,
                splitRevealImage: null,
                splitDivider: null,
                peekBeforeImage: null,
                ZoomSlider,
                ZoomValueTextBlock,
                () => _bitmap,
                () => null,
                () => null,
                () => _bitmap,
                () => false,
                () => 50d,
                setSplitPercent: null);

            _viewportController.UpdateZoomUi();
            Dispatcher.UIThread.Post(() => _viewportController?.HandleContentChanged(), DispatcherPriority.Background);
        };

        Closed += (_, _) =>
        {
            _viewportController?.Cleanup();
            _viewportController = null;
            PreviewImage.Source = null;
            _bitmap?.Dispose();
        };
    }

    private void OnZoomOutClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewportController?.ZoomOut();

    private void OnZoomInClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewportController?.ZoomIn();

    private void OnFitToWindowClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewportController?.FitToView();

    private void OnResetToHundredClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewportController?.ResetToHundred();

    private void OnCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

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
