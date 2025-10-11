using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Veyra.Desktop.Services.Navigation;

namespace Veyra.Desktop.ViewModels.Windows;

public partial class InfoWindowViewModel : ObservableObject
{
    private readonly IWindowService _windows;
    public InfoWindowViewModel(IWindowService windows) => _windows = windows;

    [RelayCommand]
    private void Close() => _windows.GetActiveWindow()?.Close();
}
