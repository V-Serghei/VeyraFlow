using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Desktop.Services.Navigation;

namespace Veyra.Desktop.ViewModels.Windows;

public partial class InfoWindowViewModel(IWindowService windows) : ObservableObject
{
    [RelayCommand]
    private void Close() => windows.GetActiveWindow()?.Close();
}
