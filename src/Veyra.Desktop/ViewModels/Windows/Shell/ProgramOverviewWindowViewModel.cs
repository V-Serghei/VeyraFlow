using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Desktop.Services.Navigation;

namespace Veyra.Desktop.ViewModels.Windows;

public sealed partial class ProgramOverviewWindowViewModel(IWindowService? windows) : ObservableObject
{
    public ProgramOverviewWindowViewModel()
        : this(null)
    {
    }

    [RelayCommand]
    private void Close() => windows?.GetActiveWindow()?.Close();
}
