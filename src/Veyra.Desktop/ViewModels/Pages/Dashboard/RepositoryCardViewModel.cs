using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Veyra.Desktop.ViewModels.Pages.Dashboard;

public sealed partial class RepositoryCardViewModel : ObservableObject
{
    public int Id { get; init; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string _directoryPath = string.Empty;

    public ObservableCollection<string> LinkedFormats { get; } = new();

    public string FormatsDisplay => LinkedFormats.Count == 0
        ? "Нет привязанных форматов"
        : string.Join(", ", LinkedFormats);

    public void RefreshFormatsDisplay() => OnPropertyChanged(nameof(FormatsDisplay));
}
