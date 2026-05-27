using Avalonia.Controls;
using Avalonia.Input;
using Veyra.Desktop.ViewModels.Pages.Search;

namespace Veyra.Desktop.Views.Pages.Search;

public partial class GlobalSearchView : UserControl
{
    public GlobalSearchView()
    {
        InitializeComponent();
    }

    private void OnFloatingPanelBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is GlobalSearchViewModel vm)
            vm.CloseFloatingPanels();

        e.Handled = true;
    }
}
