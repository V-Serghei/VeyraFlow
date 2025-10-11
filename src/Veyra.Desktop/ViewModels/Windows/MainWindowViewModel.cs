using CommunityToolkit.Mvvm.ComponentModel;

namespace Veyra.Desktop.ViewModels.Windows;

public partial class MainWindowViewModel : ObservableObject
{
    public string Greeting => "Welcome to VeyraFlow";
}
