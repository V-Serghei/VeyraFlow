using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views.Windows;

public partial class TrayPanelWindow : Window
{
    public TrayPanelWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is TrayPanelWindowViewModel vm)
            vm.RequestClose += Close;

        Deactivated += OnDeactivated;
        Dispatcher.UIThread.Post(() => PanelRoot.Opacity = 1d, DispatcherPriority.Background);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is TrayPanelWindowViewModel vm)
            vm.RequestClose -= Close;

        Deactivated -= OnDeactivated;
        PanelRoot.Opacity = 0d;
        base.OnClosed(e);
    }

    public void PositionNearTray(Window? owner = null, int marginPx = 18)
    {
        var screen = owner is not null
            ? Screens.ScreenFromVisual(owner) ?? Screens.Primary
            : Screens.Primary;
        if (screen is null)
            return;

        var workingArea = screen.WorkingArea;
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1d;
        var widthDip = Width > 0 ? Width : Bounds.Width;
        var heightDip = Height > 0 ? Height : Bounds.Height;

        var widthPx = Math.Max(1, (int)Math.Round(widthDip * scaling));
        var heightPx = Math.Max(1, (int)Math.Round(heightDip * scaling));

        var x = workingArea.X + Math.Max(0, workingArea.Width - widthPx - marginPx);
        var y = workingArea.Y + Math.Max(0, workingArea.Height - heightPx - marginPx);

        Position = new PixelPoint(x, y);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        Close();
    }
}
