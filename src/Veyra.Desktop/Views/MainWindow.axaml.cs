using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            FitToWorkingArea();
            Dispatcher.UIThread.Post(FitToWorkingArea, DispatcherPriority.Background);

            if (DataContext is MainWindowViewModel vm)
                vm.OnLoaded();
        };
    }

    private void FitToWorkingArea()
    {
        var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
        if (screen is null)
            return;

        var workingArea = screen.WorkingArea;
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1d;
        const double frameMargin = 0d;

        var availableWidth = System.Math.Max(720d, (workingArea.Width / scaling) - frameMargin);
        var availableHeight = System.Math.Max(560d, (workingArea.Height / scaling) - frameMargin);

        if (MinWidth > availableWidth)
            MinWidth = availableWidth;

        if (MinHeight > availableHeight)
            MinHeight = availableHeight;

        // Main window should open fully inside working area.
        var requestedWidth = availableWidth;
        var requestedHeight = availableHeight;

        var targetWidth = System.Math.Max(MinWidth, System.Math.Min(requestedWidth, availableWidth));
        var targetHeight = System.Math.Max(MinHeight, System.Math.Min(requestedHeight, availableHeight));

        Width = targetWidth;
        Height = targetHeight;
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;

        var targetWidthPx = System.Math.Max(1, (int)System.Math.Round(targetWidth * scaling));
        var targetHeightPx = System.Math.Max(1, (int)System.Math.Round(targetHeight * scaling));

        var x = workingArea.X + (workingArea.Width - targetWidthPx) / 2;
        var y = workingArea.Y + (workingArea.Height - targetHeightPx) / 2;

        var maxX = workingArea.X + System.Math.Max(0, workingArea.Width - targetWidthPx);
        var maxY = workingArea.Y + System.Math.Max(0, workingArea.Height - targetHeightPx);

        Position = new PixelPoint(Clamp(x, workingArea.X, maxX), Clamp(y, workingArea.Y, maxY));
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
