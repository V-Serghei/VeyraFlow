using Avalonia.Controls;
using Avalonia.Threading;
using Veyra.Desktop.Views;

namespace Veyra.Desktop.Views.Pages.SetupWizard;

public partial class SetupWizardWindow : Window
{
    public SetupWizardWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            WindowLayoutHelper.FitToWorkingArea(
                this,
                Owner as Window,
                maximizeToWorkingArea: false,
                frameMarginDip: 8d,
                minWidthDip: 800d,
                minHeightDip: 560d);

            Dispatcher.UIThread.Post(() =>
                WindowLayoutHelper.FitToWorkingArea(
                    this,
                    Owner as Window,
                    maximizeToWorkingArea: false,
                    frameMarginDip: 8d,
                    minWidthDip: 800d,
                    minHeightDip: 560d),
                DispatcherPriority.Background);
        };
    }
}
