using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Veyra.Desktop.Views;

namespace Veyra.Desktop.Views.Windows;

public partial class DetachedImagePreviewWindow : Window
{
    private const double MinZoomPercent = 1;
    private const double MaxZoomPercent = 500;

    private readonly Bitmap? _bitmap;
    private bool _suppressZoomEvents;
    private bool _zoomUserAdjusted;
    private double _zoomPercent = 100;

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

            Dispatcher.UIThread.Post(FitToView, DispatcherPriority.Background);
        };

        Closed += (_, _) =>
        {
            PreviewImage.Source = null;
            _bitmap?.Dispose();
        };
    }

    private void OnZoomOutClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _zoomUserAdjusted = true;
        ApplyZoom(_zoomPercent / 1.1d, preserveViewport: true);
    }

    private void OnZoomInClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _zoomUserAdjusted = true;
        ApplyZoom(_zoomPercent * 1.1d, preserveViewport: true);
    }

    private void OnFitToWindowClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _zoomUserAdjusted = false;
        Dispatcher.UIThread.Post(FitToView, DispatcherPriority.Background);
    }

    private void OnResetToHundredClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _zoomUserAdjusted = true;
        ApplyZoom(100d, preserveViewport: false);
    }

    private void OnCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    private void OnZoomSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressZoomEvents)
            return;

        _zoomUserAdjusted = true;
        ApplyZoom(e.NewValue, preserveViewport: true);
    }

    private void OnImagePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            return;

        e.Handled = true;
        _zoomUserAdjusted = true;
        var factor = e.Delta.Y >= 0 ? 1.1d : 1d / 1.1d;
        ApplyZoom(_zoomPercent * factor, preserveViewport: true, focusPoint: e.GetPosition(ImageScrollViewer));
    }

    private void OnImageViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_zoomUserAdjusted)
            Dispatcher.UIThread.Post(FitToView, DispatcherPriority.Background);
    }

    private void FitToView()
    {
        if (!TryGetBitmapPixelSize(_bitmap, out var bitmapSize))
            return;

        var viewport = ImageScrollViewer.Viewport;
        var availableWidth = viewport.Width > 1 ? viewport.Width : ImageScrollViewer.Bounds.Width;
        var availableHeight = viewport.Height > 1 ? viewport.Height : ImageScrollViewer.Bounds.Height;
        if (availableWidth <= 1 || availableHeight <= 1)
            return;

        var scale = Math.Min(availableWidth / bitmapSize.Width, availableHeight / bitmapSize.Height);
        var targetPercent = Math.Clamp(scale * 100d, MinZoomPercent, MaxZoomPercent);
        ApplyZoom(targetPercent, preserveViewport: false);
    }

    private void ApplyZoom(double percent, bool preserveViewport, Point? focusPoint = null)
    {
        if (!TryGetBitmapPixelSize(_bitmap, out var bitmapSize))
            return;

        var clampedPercent = Math.Clamp(percent, MinZoomPercent, MaxZoomPercent);
        var zoomingOut = clampedPercent < (_zoomPercent - 0.01d);
        var shouldPreserveViewport = preserveViewport && !zoomingOut;
        var oldWidth = ImageHost.Width > 0 ? ImageHost.Width : bitmapSize.Width;
        var oldHeight = ImageHost.Height > 0 ? ImageHost.Height : bitmapSize.Height;
        var oldOffset = ImageScrollViewer.Offset;
        var viewport = ImageScrollViewer.Viewport;
        var focus = focusPoint ?? new Point(viewport.Width / 2d, viewport.Height / 2d);

        var relativeX = oldWidth > 0 ? Math.Clamp((oldOffset.X + focus.X) / oldWidth, 0d, 1d) : 0d;
        var relativeY = oldHeight > 0 ? Math.Clamp((oldOffset.Y + focus.Y) / oldHeight, 0d, 1d) : 0d;

        var newWidth = Math.Max(1d, bitmapSize.Width * clampedPercent / 100d);
        var newHeight = Math.Max(1d, bitmapSize.Height * clampedPercent / 100d);

        PreviewImage.Width = newWidth;
        PreviewImage.Height = newHeight;
        ImageHost.Width = newWidth;
        ImageHost.Height = newHeight;
        ImageHost.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        ImageHost.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;

        ImageViewportHost.Width = newWidth;
        ImageViewportHost.Height = newHeight;
        ImageViewportHost.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        ImageViewportHost.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;

        _zoomPercent = clampedPercent;
        UpdateZoomUi();

        Dispatcher.UIThread.Post(() =>
        {
            var currentViewport = ImageScrollViewer.Viewport;
            var currentViewportWidth = currentViewport.Width > 1 ? currentViewport.Width : ImageScrollViewer.Bounds.Width;
            var currentViewportHeight = currentViewport.Height > 1 ? currentViewport.Height : ImageScrollViewer.Bounds.Height;
            var maxOffsetX = Math.Max(0d, newWidth - currentViewportWidth);
            var maxOffsetY = Math.Max(0d, newHeight - currentViewportHeight);

            if (!shouldPreserveViewport)
            {
                ImageScrollViewer.Offset = new Vector(0d, 0d);
                return;
            }

            var targetX = maxOffsetX <= 0d || newWidth <= currentViewportWidth + 0.5d
                ? 0d
                : Math.Clamp((newWidth * relativeX) - focus.X, 0d, maxOffsetX);
            var targetY = maxOffsetY <= 0d || newHeight <= currentViewportHeight + 0.5d
                ? 0d
                : Math.Clamp((newHeight * relativeY) - focus.Y, 0d, maxOffsetY);
            ImageScrollViewer.Offset = new Vector(targetX, targetY);
        }, DispatcherPriority.Background);
    }

    private void UpdateZoomUi()
    {
        _suppressZoomEvents = true;
        try
        {
            ZoomSlider.Value = _zoomPercent;
        }
        finally
        {
            _suppressZoomEvents = false;
        }

        ZoomValueTextBlock.Text = $"{Math.Round(_zoomPercent):0}%";
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
