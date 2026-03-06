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
    [ObservableProperty] private string _statusText = "Локально";
    [ObservableProperty] private string _statusColor = "#9E9E9E";
    [ObservableProperty] private string _lastActivity = "Только что";

    [ObservableProperty] private int _fileCount;
    [ObservableProperty] private int _versionCount;
    [ObservableProperty] private string _sizeDisplay = "0 МБ";

    public ObservableCollection<string> LinkedFormats { get; } = new();

    public string FormatsDisplay => LinkedFormats.Count == 0
        ? "Нет форматов"
        : string.Join("  ", LinkedFormats);

    public string FormatsBadge => LinkedFormats.Count == 0
        ? "—"
        : $"{LinkedFormats.Count} формат(ов)";

    public string ShortPath
    {
        get
        {
            if (string.IsNullOrEmpty(DirectoryPath)) return "";
            var parts = DirectoryPath.Replace('/', '\\').Split('\\');
            return parts.Length <= 2
                ? DirectoryPath
                : "...\\" + string.Join("\\", parts.Skip(parts.Length - 2));
        }
    }

    public void RefreshFormatsDisplay()
    {
        OnPropertyChanged(nameof(FormatsDisplay));
        OnPropertyChanged(nameof(FormatsBadge));
        OnPropertyChanged(nameof(ShortPath));
    }
}