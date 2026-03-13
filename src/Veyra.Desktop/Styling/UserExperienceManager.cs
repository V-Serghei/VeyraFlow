using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Veyra.Desktop.Localization;

namespace Veyra.Desktop.Styling;

public sealed record UserExperienceOption(string Code, string LocalizationKey, string DescriptionKey);

public sealed class UserExperienceManager : INotifyPropertyChanged
{
    private readonly LocalizationSettingsStore _settingsStore = new();
    private string _currentModeCode = "basic";

    public static UserExperienceManager Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ModeChanged;

    public IReadOnlyList<UserExperienceOption> AvailableModes { get; } = new ReadOnlyCollection<UserExperienceOption>(
    [
        new UserExperienceOption("basic", "experience.option.basic", "experience.option.basic.description"),
        new UserExperienceOption("professional", "experience.option.professional", "experience.option.professional.description")
    ]);

    public string CurrentModeCode
    {
        get => _currentModeCode;
        private set
        {
            if (string.Equals(_currentModeCode, value, StringComparison.OrdinalIgnoreCase))
                return;

            _currentModeCode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentModeCode)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBasicMode)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsProfessionalMode)));
            ModeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsBasicMode => string.Equals(CurrentModeCode, "basic", StringComparison.OrdinalIgnoreCase);
    public bool IsProfessionalMode => string.Equals(CurrentModeCode, "professional", StringComparison.OrdinalIgnoreCase);

    private UserExperienceManager()
    {
        var saved = _settingsStore.LoadExperienceCode();
        SetMode(string.IsNullOrWhiteSpace(saved) ? "basic" : saved, persist: false);
    }

    public bool SetMode(string? modeCode, bool persist = true)
    {
        var normalized = Normalize(modeCode);
        if (normalized is null)
            return false;

        CurrentModeCode = normalized;
        if (persist)
            _settingsStore.SaveExperienceCode(normalized);

        return true;
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "basic";

        return value.Trim().ToLowerInvariant() switch
        {
            "basic" => "basic",
            "professional" => "professional",
            "pro" => "professional",
            _ => null
        };
    }
}
