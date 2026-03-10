using Avalonia;
using Avalonia.Controls;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views.Windows;

public partial class SnapshotNameDialogWindow : Window
{
    private bool _isSyncingDiffScroll;
    private ScrollViewer? _leftDiffScrollViewer;
    private ScrollViewer? _rightDiffScrollViewer;

    public bool IsConfirmed { get; private set; }
    public string? SnapshotTitle { get; private set; }

    public SnapshotNameDialogWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            FitToWorkingArea();
            _leftDiffScrollViewer = this.FindControl<ScrollViewer>("LeftDiffScrollViewer");
            _rightDiffScrollViewer = this.FindControl<ScrollViewer>("RightDiffScrollViewer");

            if (DataContext is SnapshotNameDialogWindowViewModel vm)
                vm.RequestClose += OnRequestClose;
        };

        Closed += (_, _) =>
        {
            if (DataContext is SnapshotNameDialogWindowViewModel vm)
            {
                vm.RequestClose -= OnRequestClose;
                vm.CleanupPreviewResources();
            }
        };
    }

    private void OnRequestClose(bool confirmed)
    {
        IsConfirmed = confirmed;

        if (confirmed && DataContext is SnapshotNameDialogWindowViewModel vm)
            SnapshotTitle = vm.SnapshotName;
        else
            SnapshotTitle = null;

        Close();
    }

    private void OnLeftDiffScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer source)
            return;

        var offset = source.GetValue(ScrollViewer.OffsetProperty);
        SyncDiffScroll(fromLeft: true, offset);
    }

    private void OnRightDiffScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer source)
            return;

        var offset = source.GetValue(ScrollViewer.OffsetProperty);
        SyncDiffScroll(fromLeft: false, offset);
    }

    private void SyncDiffScroll(bool fromLeft, Vector offset)
    {
        if (_isSyncingDiffScroll)
            return;

        if (DataContext is SnapshotNameDialogWindowViewModel vm)
            vm.UpdatePinnedHunkHeaderByScroll(offset.Y);

        if (_leftDiffScrollViewer is null || _rightDiffScrollViewer is null)
            return;

        ScrollViewer target = fromLeft ? _rightDiffScrollViewer : _leftDiffScrollViewer;
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
        var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
        if (screen is null)
            return;

        var workingArea = screen.WorkingArea;
        const double frameMargin = 32d;

        var availableWidth = System.Math.Max(640d, workingArea.Width - frameMargin);
        var availableHeight = System.Math.Max(480d, workingArea.Height - frameMargin);

        if (MinWidth > availableWidth)
            MinWidth = availableWidth;

        if (MinHeight > availableHeight)
            MinHeight = availableHeight;

        var requestedWidth = Width > 0 ? Width : availableWidth;
        var requestedHeight = Height > 0 ? Height : availableHeight;

        var targetWidth = System.Math.Min(requestedWidth, availableWidth);
        var targetHeight = System.Math.Min(requestedHeight, availableHeight);

        if (targetWidth < MinWidth)
            targetWidth = availableWidth;

        if (targetHeight < MinHeight)
            targetHeight = availableHeight;

        Width = targetWidth;
        Height = targetHeight;
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;

        var x = workingArea.X + System.Math.Max(0, (int)System.Math.Round((workingArea.Width - targetWidth) / 2d));
        var y = workingArea.Y + System.Math.Max(0, (int)System.Math.Round((workingArea.Height - targetHeight) / 2d));
        Position = new PixelPoint(x, y);
    }
}
