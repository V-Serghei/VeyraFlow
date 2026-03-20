using System;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Avalonia;
using Veyra.Desktop.Services.Shell.Tray;
using Veyra.Desktop.ViewModels.Windows;
using System.ComponentModel;
using System.Linq;

namespace Veyra.Desktop.Views;

public partial class MainWindow : Window
{
    private const double CompactWidth = 1120;
    private const double NarrowWidth = 900;
    private const double GuidedTourBubbleWidth = 360;
    private const double GuidedTourBubblePadding = 18;
    private MainWindowViewModel? _tourViewModel;
    private bool _isGuidedTourRelayoutQueued;
    private readonly IAppTrayService? _trayService;

    public MainWindow()
    {
        _trayService = App._serviceProvider?.GetService(typeof(IAppTrayService)) as IAppTrayService;
        InitializeComponent();
        DataContextChanged += (_, _) => AttachTourViewModel();
        SizeChanged += OnSizeChanged;
        Closing += OnClosing;
        Opened += (_, _) =>
        {
            WindowLayoutHelper.FitToWorkingArea(
                this,
                maximizeToWorkingArea: true,
                frameMarginDip: 0d,
                minWidthDip: 720d,
                minHeightDip: 560d);

            Dispatcher.UIThread.Post(() =>
                WindowLayoutHelper.FitToWorkingArea(
                    this,
                    maximizeToWorkingArea: true,
                    frameMarginDip: 0d,
                    minWidthDip: 720d,
                    minHeightDip: 560d),
                DispatcherPriority.Background);

            ApplyResponsiveLayout(Bounds.Width);
            AttachTourViewModel();

            if (DataContext is MainWindowViewModel vm)
                vm.OnLoaded();
        };
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
        => _trayService?.HandleMainWindowClosing(this, e);

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
        QueueGuidedTourLayoutUpdate();
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

    private void AttachTourViewModel()
    {
        if (_tourViewModel is not null)
            _tourViewModel.PropertyChanged -= OnTourViewModelPropertyChanged;

        _tourViewModel = DataContext as MainWindowViewModel;
        if (_tourViewModel is not null)
            _tourViewModel.PropertyChanged += OnTourViewModelPropertyChanged;

        QueueGuidedTourLayoutUpdate();
    }

    private void OnTourViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsGuidedTourVisible)
            or nameof(MainWindowViewModel.GuidedTourTargetName)
            or nameof(MainWindowViewModel.CurrentPage))
        {
            QueueGuidedTourLayoutUpdate();
        }
    }

    private void QueueGuidedTourLayoutUpdate()
    {
        Dispatcher.UIThread.Post(UpdateGuidedTourLayout, DispatcherPriority.Loaded);
    }

    private void UpdateGuidedTourLayout()
    {
        if (_tourViewModel is null || !_tourViewModel.IsGuidedTourVisible || GuidedTourOverlayRoot.Bounds.Width <= 0)
            return;

        var target = FindNamedControl(_tourViewModel.GuidedTourTargetName);
        if (target is null || !target.IsVisible)
        {
            GuidedTourHighlight.IsVisible = false;
            CenterGuidedTourBubble();
            return;
        }

        target.BringIntoView();

        var origin = target.TranslatePoint(new Point(0, 0), GuidedTourOverlayRoot);
        if (origin is null)
        {
            GuidedTourHighlight.IsVisible = false;
            CenterGuidedTourBubble();
            return;
        }

        var highlightRect = new Rect(
            origin.Value.X - 8,
            origin.Value.Y - 8,
            target.Bounds.Width + 16,
            target.Bounds.Height + 16);

        if (highlightRect.X < 0
            || highlightRect.Y < 0
            || highlightRect.Right > GuidedTourOverlayRoot.Bounds.Width
            || highlightRect.Bottom > GuidedTourOverlayRoot.Bounds.Height)
        {
            QueueSecondaryGuidedTourLayoutUpdate();
        }

        GuidedTourHighlight.IsVisible = true;
        GuidedTourHighlight.Width = Math.Max(0, highlightRect.Width);
        GuidedTourHighlight.Height = Math.Max(0, highlightRect.Height);
        Canvas.SetLeft(GuidedTourHighlight, highlightRect.X);
        Canvas.SetTop(GuidedTourHighlight, highlightRect.Y);

        PositionGuidedTourBubble(highlightRect);
    }

    private Control? FindNamedControl(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return this.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(control =>
                string.Equals(control.Name, name, System.StringComparison.Ordinal) &&
                control.IsVisible);
    }

    private void CenterGuidedTourBubble()
    {
        var width = GuidedTourBubble.Bounds.Width > 0 ? GuidedTourBubble.Bounds.Width : GuidedTourBubbleWidth;
        var height = GuidedTourBubble.Bounds.Height > 0 ? GuidedTourBubble.Bounds.Height : 240;
        var left = Math.Max(GuidedTourBubblePadding, (GuidedTourOverlayRoot.Bounds.Width - width) / 2);
        var top = Math.Max(GuidedTourBubblePadding, (GuidedTourOverlayRoot.Bounds.Height - height) / 2);
        Canvas.SetLeft(GuidedTourBubble, left);
        Canvas.SetTop(GuidedTourBubble, top);
    }

    private void PositionGuidedTourBubble(Rect targetRect)
    {
        var bubbleWidth = GuidedTourBubble.Bounds.Width > 0 ? GuidedTourBubble.Bounds.Width : GuidedTourBubbleWidth;
        var bubbleHeight = GuidedTourBubble.Bounds.Height > 0 ? GuidedTourBubble.Bounds.Height : 240;
        var overlayWidth = GuidedTourOverlayRoot.Bounds.Width;
        var overlayHeight = GuidedTourOverlayRoot.Bounds.Height;
        var margin = GuidedTourBubblePadding;
        var gap = 16d;

        var preferredLeft = Clamp(targetRect.X, margin, overlayWidth - bubbleWidth - margin);
        var belowTop = targetRect.Bottom + gap;
        if (FitsBubble(preferredLeft, belowTop, bubbleWidth, bubbleHeight, overlayWidth, overlayHeight, margin))
        {
            Canvas.SetLeft(GuidedTourBubble, preferredLeft);
            Canvas.SetTop(GuidedTourBubble, belowTop);
            return;
        }

        var aboveTop = targetRect.Y - bubbleHeight - gap;
        if (FitsBubble(preferredLeft, aboveTop, bubbleWidth, bubbleHeight, overlayWidth, overlayHeight, margin))
        {
            Canvas.SetLeft(GuidedTourBubble, preferredLeft);
            Canvas.SetTop(GuidedTourBubble, aboveTop);
            return;
        }

        var sideTop = Clamp(targetRect.Y, margin, overlayHeight - bubbleHeight - margin);
        var rightLeft = targetRect.Right + gap;
        if (FitsBubble(rightLeft, sideTop, bubbleWidth, bubbleHeight, overlayWidth, overlayHeight, margin))
        {
            Canvas.SetLeft(GuidedTourBubble, rightLeft);
            Canvas.SetTop(GuidedTourBubble, sideTop);
            return;
        }

        var leftLeft = targetRect.X - bubbleWidth - gap;
        if (FitsBubble(leftLeft, sideTop, bubbleWidth, bubbleHeight, overlayWidth, overlayHeight, margin))
        {
            Canvas.SetLeft(GuidedTourBubble, leftLeft);
            Canvas.SetTop(GuidedTourBubble, sideTop);
            return;
        }

        Canvas.SetLeft(GuidedTourBubble, preferredLeft);
        Canvas.SetTop(GuidedTourBubble, Clamp(targetRect.Y + (targetRect.Height - bubbleHeight) / 2, margin, overlayHeight - bubbleHeight - margin));
    }

    private void QueueSecondaryGuidedTourLayoutUpdate()
    {
        if (_isGuidedTourRelayoutQueued)
            return;

        _isGuidedTourRelayoutQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _isGuidedTourRelayoutQueued = false;
            UpdateGuidedTourLayout();
        }, DispatcherPriority.Background);
    }

    private static bool FitsBubble(double left, double top, double width, double height, double overlayWidth, double overlayHeight, double margin)
        => left >= margin
           && top >= margin
           && left + width <= overlayWidth - margin
           && top + height <= overlayHeight - margin;

    private static double Clamp(double value, double min, double max)
    {
        if (max < min)
            return min;

        return Math.Max(min, Math.Min(max, value));
    }
}
