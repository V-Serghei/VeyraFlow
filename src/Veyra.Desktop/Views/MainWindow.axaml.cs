using Avalonia.Controls;
using Avalonia.Threading;
using Veyra.Desktop.ViewModels.Windows;

namespace Veyra.Desktop.Views;

public partial class MainWindow : Window
{
    private const double CompactWidth = 1120;
    private const double NarrowWidth = 900;

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
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

            if (DataContext is MainWindowViewModel vm)
                vm.OnLoaded();
        };
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
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
}
