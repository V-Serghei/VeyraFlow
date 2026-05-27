namespace Veyra.Desktop.ViewModels.Windows;

public sealed class TrayPanelRepositoryOptionViewModel
{
    public TrayPanelRepositoryOptionViewModel(int id, string name)
    {
        Id = id;
        Name = name;
    }

    public int Id { get; }
    public string Name { get; }
}
