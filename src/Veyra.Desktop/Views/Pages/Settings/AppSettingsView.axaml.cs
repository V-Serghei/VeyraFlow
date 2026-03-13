using Avalonia;
using Avalonia.Controls;

namespace Veyra.Desktop.Views.Pages.Settings;

public partial class AppSettingsView : UserControl
{
    private const double CompactWidth = 1280;
    private const double NarrowWidth = 1080;

    public AppSettingsView()
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
            SettingsLayoutGrid.ColumnDefinitions = new ColumnDefinitions("*");
            SettingsLayoutGrid.RowDefinitions = new RowDefinitions("Auto,0,*");

            Grid.SetColumn(SettingsSidebarPanel, 0);
            Grid.SetRow(SettingsSidebarPanel, 0);

            Grid.SetColumn(SettingsLayoutSplitter, 0);
            Grid.SetRow(SettingsLayoutSplitter, 1);
            SettingsLayoutSplitter.IsVisible = false;

            Grid.SetColumn(SettingsContentPanel, 0);
            Grid.SetRow(SettingsContentPanel, 2);
            SettingsContentPanel.Margin = new Thickness(0, 12, 0, 0);
            return;
        }

        SettingsLayoutGrid.RowDefinitions = new RowDefinitions("*");
        SettingsLayoutGrid.ColumnDefinitions = width < CompactWidth
            ? new ColumnDefinitions("220,8,*")
            : new ColumnDefinitions("250,8,*");

        Grid.SetColumn(SettingsSidebarPanel, 0);
        Grid.SetRow(SettingsSidebarPanel, 0);

        Grid.SetColumn(SettingsLayoutSplitter, 1);
        Grid.SetRow(SettingsLayoutSplitter, 0);
        SettingsLayoutSplitter.IsVisible = true;

        Grid.SetColumn(SettingsContentPanel, 2);
        Grid.SetRow(SettingsContentPanel, 0);
        SettingsContentPanel.Margin = new Thickness(12, 0, 0, 0);
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
