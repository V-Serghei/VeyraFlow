using CommunityToolkit.Mvvm.ComponentModel;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.ViewModels.Pages.Settings;

public sealed partial class AppSettingsTabViewModel : ObservableObject
{
    public AppSettingsTabViewModel(string key, string titleKey, string subtitleKey)
    {
        Key = key;
        TitleKey = titleKey;
        SubtitleKey = subtitleKey;
    }

    public string Key { get; }
    public string TitleKey { get; }
    public string SubtitleKey { get; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isVisible = true;

    public string Title => Loc.T(TitleKey);
    public string Subtitle => Loc.T(SubtitleKey);

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
    }
}
