using Avalonia;
using Avalonia.Controls;

namespace Veyra.Desktop.Views.Pages.RepositorySettings;

public partial class RepositorySettingsView : UserControl
{
    private const double CompactWidth = 1360;
    private const double NarrowWidth = 1120;

    public RepositorySettingsView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        AttachedToVisualTree += (_, _) => ApplyResponsiveLayout(Bounds.Width);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        ToggleRootClass("compact-layout", width < CompactWidth);
        ToggleRootClass("narrow-layout", width < NarrowWidth);

        if (width < NarrowWidth)
        {
            RepositorySettingsLayoutGrid.ColumnDefinitions = new ColumnDefinitions("*");
            RepositorySettingsLayoutGrid.RowDefinitions = new RowDefinitions("Auto,14,*");

            Grid.SetColumn(RepositorySettingsPrimaryColumn, 0);
            Grid.SetRow(RepositorySettingsPrimaryColumn, 0);

            Grid.SetColumn(RepositorySettingsSecondaryColumn, 0);
            Grid.SetRow(RepositorySettingsSecondaryColumn, 2);
            return;
        }

        RepositorySettingsLayoutGrid.RowDefinitions = new RowDefinitions("*");
        RepositorySettingsLayoutGrid.ColumnDefinitions = width < CompactWidth
            ? new ColumnDefinitions("1*,0.94*")
            : new ColumnDefinitions("1.05*,0.95*");

        Grid.SetColumn(RepositorySettingsPrimaryColumn, 0);
        Grid.SetRow(RepositorySettingsPrimaryColumn, 0);

        Grid.SetColumn(RepositorySettingsSecondaryColumn, 1);
        Grid.SetRow(RepositorySettingsSecondaryColumn, 0);
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
