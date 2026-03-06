using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Veyra.Desktop.ViewModels.Pages.Settings;

public sealed partial class AppSettingsTabViewModel : ObservableObject
{
    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
}

public sealed partial class AppSettingsViewModel : ObservableObject
{
    [ObservableProperty] private AppSettingsTabViewModel? _selectedTab;

    public event Action? BackRequested;

    public ObservableCollection<AppSettingsTabViewModel> Tabs { get; } =
    [
        new() { Key = "general", Title = "Общие", Subtitle = "Интерфейс, язык, уведомления" },
        new() { Key = "snapshots", Title = "Снапшоты", Subtitle = "Расписание, частота, триггеры" },
        new() { Key = "ignore", Title = "Игнор", Subtitle = "Исключения путей и форматов" },
        new() { Key = "cleanup", Title = "Очистка", Subtitle = "Политика хранения и удаление" },
        new() { Key = "security", Title = "Безопасность", Subtitle = "Шифрование и ключи" },
        new() { Key = "user", Title = "Пользователь", Subtitle = "Профиль и сессия" },
        new() { Key = "sync", Title = "Синхронизация", Subtitle = "Локальный режим и облако" },
        new() { Key = "diagnostics", Title = "Диагностика", Subtitle = "Логи, трассировка и экспорт" }
    ];

    public AppSettingsViewModel()
    {
        SelectedTab = Tabs.FirstOrDefault();
    }

    [RelayCommand]
    private void Back()
    {
        BackRequested?.Invoke();
    }

    [RelayCommand]
    private void SelectTab(AppSettingsTabViewModel? tab)
    {
        if (tab is not null)
            SelectedTab = tab;
    }
}
