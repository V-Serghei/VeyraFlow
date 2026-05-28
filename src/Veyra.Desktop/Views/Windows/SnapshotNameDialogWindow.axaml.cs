using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Veyra.Desktop.Localization;
using Veyra.Desktop.Services.Preview;
using Veyra.Desktop.Views;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views.Windows;

public partial class SnapshotNameDialogWindow : Window
{
    private const double CompactWidth = 1120;
    private const double NarrowWidth = 940;
    private bool _isSyncingDiffScroll;
    private ScrollViewer? _leftDiffScrollViewer;
    private ScrollViewer? _rightDiffScrollViewer;
    private InteractiveImageViewportController? _overlayViewportController;
    private SnapshotNameDialogWindowViewModel? _viewModel;
    private bool _isCleaningUp;

    public bool IsConfirmed { get; private set; }
    public string? SnapshotTitle { get; private set; }
    public IReadOnlyList<string> SnapshotTags { get; private set; } = Array.Empty<string>();

    public SnapshotNameDialogWindow()
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
                minWidthDip: 760d,
                minHeightDip: 540d);

            Dispatcher.UIThread.Post(() =>
                WindowLayoutHelper.FitToWorkingArea(
                    this,
                    ownerWindow,
                    maximizeToWorkingArea: false,
                    frameMarginDip: 8d,
                    minWidthDip: 760d,
                    minHeightDip: 540d),
                DispatcherPriority.Background);

            ApplyResponsiveLayout(Bounds.Width);

            _leftDiffScrollViewer = this.FindControl<ScrollViewer>("LeftDiffScrollViewer");
            _rightDiffScrollViewer = this.FindControl<ScrollViewer>("RightDiffScrollViewer");
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

            if (DataContext is SnapshotNameDialogWindowViewModel vm)
            {
                AttachViewModel(vm);
                vm.RequestClose += OnRequestClose;
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
            }

            _viewModel = null;
            DataContext = null;
            vm?.CleanupPreviewResources();
            _leftDiffScrollViewer = null;
            _rightDiffScrollViewer = null;
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

    private void OnRequestClose(bool confirmed)
    {
        IsConfirmed = confirmed;

        if (confirmed && DataContext is SnapshotNameDialogWindowViewModel vm)
        {
            SnapshotTitle = vm.SnapshotName;
            SnapshotTags = vm.NormalizedSnapshotTags.ToArray();
        }
        else
        {
            SnapshotTitle = null;
            SnapshotTags = Array.Empty<string>();
        }

        Close();
    }

    private void OnOpenDetachedImagePreviewClicked(object? sender, RoutedEventArgs e)
    {
        if (GetViewModel() is not SnapshotNameDialogWindowViewModel vm)
            return;

        var pngBytes = vm.GetCurrentImageDiffPreviewPngBytes();
        if (pngBytes.Length == 0)
            return;

        var previewTitle = string.IsNullOrWhiteSpace(vm.OverlayImageCaption)
            ? Loc.T("common.preview")
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

    private void OnSnapshotTagInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (GetViewModel()?.AddSnapshotTagCommand.CanExecute(null) == true)
        {
            GetViewModel()?.AddSnapshotTagCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnTagInputLostFocus(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(
            () => GetViewModel()?.ClearTagSuggestions(),
            DispatcherPriority.Background);
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

    private void AttachViewModel(SnapshotNameDialogWindowViewModel vm)
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
            case nameof(SnapshotNameDialogWindowViewModel.OverlayImagePreview):
            case nameof(SnapshotNameDialogWindowViewModel.LeftImagePreview):
            case nameof(SnapshotNameDialogWindowViewModel.RightImagePreview):
            case nameof(SnapshotNameDialogWindowViewModel.SelectedImageDiffMode):
                _overlayViewportController?.HandleContentChanged();
                return;

            case nameof(SnapshotNameDialogWindowViewModel.ShowImageDiffDetails):
            case nameof(SnapshotNameDialogWindowViewModel.ShowImageDiffSettings):
            case nameof(SnapshotNameDialogWindowViewModel.ShowSourceImagePanels):
                Dispatcher.UIThread.Post(() => _overlayViewportController?.HandleContentChanged(), DispatcherPriority.Background);
                return;

            case nameof(SnapshotNameDialogWindowViewModel.ComparisonSplitPercent):
                _overlayViewportController?.HandleSplitPercentChanged();
                return;
        }
    }

    private Bitmap? GetOverlayReferenceBitmap()
    {
        if (_isCleaningUp)
            return null;

        if (GetViewModel() is not SnapshotNameDialogWindowViewModel vm)
            return null;

        if (vm.IsSplitImageDiffMode)
            return vm.LeftImagePreview ?? vm.RightImagePreview ?? vm.OverlayImagePreview;

        return vm.OverlayImagePreview ?? vm.RightImagePreview ?? vm.LeftImagePreview;
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

    private void OnImageDiffSettingsBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        GetViewModel()?.HideImageDiffSettingsPane();
        e.Handled = true;
    }

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

    private SnapshotNameDialogWindowViewModel? GetViewModel()
        => _viewModel ?? DataContext as SnapshotNameDialogWindowViewModel;

}
