using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views.Windows;

public partial class FileVersionCompareWindow : Window
{
    private bool _isSyncingDiffScroll;
    private ScrollViewer? _leftDiffScrollViewer;
    private ScrollViewer? _rightDiffScrollViewer;
    private ScrollViewer? _leftWordDiffScrollViewer;
    private ScrollViewer? _rightWordDiffScrollViewer;

    public FileVersionCompareWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            FitToWorkingArea();
            Dispatcher.UIThread.Post(FitToWorkingArea, DispatcherPriority.Background);

            _leftDiffScrollViewer = this.FindControl<ScrollViewer>("LeftDiffScrollViewer");
            _rightDiffScrollViewer = this.FindControl<ScrollViewer>("RightDiffScrollViewer");
            _leftWordDiffScrollViewer = this.FindControl<ScrollViewer>("LeftWordDiffScrollViewer");
            _rightWordDiffScrollViewer = this.FindControl<ScrollViewer>("RightWordDiffScrollViewer");

            if (DataContext is FileVersionCompareWindowViewModel vm)
                vm.RequestClose += OnRequestClose;
        };

        Closed += (_, _) =>
        {
            if (DataContext is FileVersionCompareWindowViewModel vm)
            {
                vm.RequestClose -= OnRequestClose;
                vm.CleanupPreviewResources();
            }
        };
    }

    private void OnRequestClose() => Close();

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

    private void FitToWorkingArea()
    {
        var screen = ResolveTargetScreen();
        if (screen is null)
            return;

        var workingArea = screen.WorkingArea;
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1d;
        const double frameMargin = 24d;

        var availableWidth = System.Math.Max(640d, (workingArea.Width / scaling) - frameMargin);
        var availableHeight = System.Math.Max(480d, (workingArea.Height / scaling) - frameMargin);

        if (MinWidth > availableWidth)
            MinWidth = availableWidth;

        if (MinHeight > availableHeight)
            MinHeight = availableHeight;

        var requestedWidth = Width > 0 ? Width : availableWidth;
        var requestedHeight = Height > 0 ? Height : availableHeight;

        var targetWidth = System.Math.Max(MinWidth, System.Math.Min(requestedWidth, availableWidth));
        var targetHeight = System.Math.Max(MinHeight, System.Math.Min(requestedHeight, availableHeight));

        Width = targetWidth;
        Height = targetHeight;
        MaxWidth = availableWidth;
        MaxHeight = availableHeight;

        var targetWidthPx = System.Math.Max(1, (int)System.Math.Round(targetWidth * scaling));
        var targetHeightPx = System.Math.Max(1, (int)System.Math.Round(targetHeight * scaling));

        var desiredX = workingArea.X + (workingArea.Width - targetWidthPx) / 2;
        var desiredY = workingArea.Y + (workingArea.Height - targetHeightPx) / 2;

        if (Owner is Window owner)
        {
            var ownerWidthDip = owner.Bounds.Width > 0 ? owner.Bounds.Width : owner.Width;
            var ownerHeightDip = owner.Bounds.Height > 0 ? owner.Bounds.Height : owner.Height;

            var ownerWidthPx = System.Math.Max(1, (int)System.Math.Round(ownerWidthDip * scaling));
            var ownerHeightPx = System.Math.Max(1, (int)System.Math.Round(ownerHeightDip * scaling));

            desiredX = owner.Position.X + (ownerWidthPx - targetWidthPx) / 2;
            desiredY = owner.Position.Y + (ownerHeightPx - targetHeightPx) / 2;
        }

        var minX = workingArea.X;
        var minY = workingArea.Y;
        var maxX = workingArea.X + System.Math.Max(0, workingArea.Width - targetWidthPx);
        var maxY = workingArea.Y + System.Math.Max(0, workingArea.Height - targetHeightPx);

        Position = new PixelPoint(Clamp(desiredX, minX, maxX), Clamp(desiredY, minY, maxY));
    }

    private Screen? ResolveTargetScreen()
    {
        if (Owner is Window owner)
            return Screens.ScreenFromVisual(owner) ?? Screens.ScreenFromVisual(this) ?? Screens.Primary;

        return Screens.ScreenFromVisual(this) ?? Screens.Primary;
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
            return min;

        if (value > max)
            return max;

        return value;
    }
}
