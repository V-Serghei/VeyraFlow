using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Veyra.Desktop.Views;
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
}
