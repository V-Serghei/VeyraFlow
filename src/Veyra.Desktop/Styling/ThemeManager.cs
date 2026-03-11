using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Styling;
using Veyra.Desktop.Localization;
using AvaloniaApplication = Avalonia.Application;

namespace Veyra.Desktop.Styling;

public sealed record ThemeOption(string Code, string LocalizationKey);

public sealed class ThemeManager : INotifyPropertyChanged
{
    private readonly LocalizationSettingsStore _settingsStore = new();
    private string _currentThemeCode = "system";

    public static ThemeManager Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ThemeChanged;

    public IReadOnlyList<ThemeOption> AvailableThemes { get; } = new ReadOnlyCollection<ThemeOption>(
    [
        new ThemeOption("system", "theme.option.system"),
        new ThemeOption("light", "theme.option.light"),
        new ThemeOption("dark", "theme.option.dark")
    ]);

    public string CurrentThemeCode
    {
        get => _currentThemeCode;
        private set
        {
            if (string.Equals(_currentThemeCode, value, StringComparison.OrdinalIgnoreCase))
                return;

            _currentThemeCode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentThemeCode)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDarkTheme)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLightTheme)));
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsDarkTheme => string.Equals(CurrentThemeCode, "dark", StringComparison.OrdinalIgnoreCase);
    public bool IsLightTheme => string.Equals(CurrentThemeCode, "light", StringComparison.OrdinalIgnoreCase);

    private ThemeManager()
    {
        var saved = _settingsStore.LoadThemeCode();
        SetTheme(string.IsNullOrWhiteSpace(saved) ? "system" : saved, persist: false);
    }

    public bool SetTheme(string? themeCode, bool persist = true)
    {
        var normalized = NormalizeThemeCode(themeCode);
        if (normalized is null)
            return false;

        ApplyThemeVariant(normalized);
        CurrentThemeCode = normalized;

        if (persist)
            _settingsStore.SaveThemeCode(normalized);

        return true;
    }

    public void ToggleDarkLight()
    {
        if (string.Equals(CurrentThemeCode, "dark", StringComparison.OrdinalIgnoreCase))
            SetTheme("light");
        else
            SetTheme("dark");
    }

    public void ApplyCurrentTheme() => ApplyThemeVariant(CurrentThemeCode);

    private static void ApplyThemeVariant(string themeCode)
    {
        if (AvaloniaApplication.Current is null)
            return;

        AvaloniaApplication.Current.RequestedThemeVariant = themeCode switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    private static string? NormalizeThemeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "system";

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "light" => "light",
            "dark" => "dark",
            "system" => "system",
            _ => null
        };
    }
}
