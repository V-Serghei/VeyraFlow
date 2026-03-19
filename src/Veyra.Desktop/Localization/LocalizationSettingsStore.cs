using System;
using System.IO;
using System.Text.Json;

namespace Veyra.Desktop.Localization;

internal sealed class LocalizationSettingsStore
{
    private const string LanguageProperty = "language";
    private const string ThemeProperty = "theme";
    private const string ExperienceProperty = "experience";

    private readonly string _settingsPath;

    public LocalizationSettingsStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow");

        _settingsPath = Path.Combine(root, "ui-settings.json");
    }

    public string? LoadLanguageCode()
    {
        try
        {
            var model = LoadModel();
            return model?.language;
        }
        catch
        {
            return null;
        }
    }

    public void SaveLanguageCode(string languageCode)
        => SaveModel(languageCode, LoadThemeCode(), LoadExperienceCode());

    public string? LoadThemeCode()
    {
        try
        {
            var model = LoadModel();
            return model?.theme;
        }
        catch
        {
            return null;
        }
    }

    public void SaveThemeCode(string themeCode)
        => SaveModel(LoadLanguageCode(), themeCode, LoadExperienceCode());

    public string? LoadExperienceCode()
    {
        try
        {
            var model = LoadModel();
            return model?.experience;
        }
        catch
        {
            return null;
        }
    }

    public void SaveExperienceCode(string experienceCode)
        => SaveModel(LoadLanguageCode(), LoadThemeCode(), experienceCode);

    private void SaveModel(string? languageCode, string? themeCode, string? experienceCode)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var model = new LocalizationSettingsModel(
                language: string.IsNullOrWhiteSpace(languageCode) ? null : languageCode.Trim(),
                theme: string.IsNullOrWhiteSpace(themeCode) ? null : themeCode.Trim(),
                experience: string.IsNullOrWhiteSpace(experienceCode) ? null : experienceCode.Trim(),
                updatedAtUtc: DateTime.UtcNow);

            var json = JsonSerializer.Serialize(model, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // Ignore persistence errors: localization/theme fallback will still work.
        }
    }

    private LocalizationSettingsModel? LoadModel()
    {
        if (!File.Exists(_settingsPath))
            return null;

        using var stream = File.OpenRead(_settingsPath);
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        var language = document.RootElement.TryGetProperty(LanguageProperty, out var languageElement)
            ? languageElement.GetString()
            : null;

        var theme = document.RootElement.TryGetProperty(ThemeProperty, out var themeElement)
            ? themeElement.GetString()
            : null;

        var experience = document.RootElement.TryGetProperty(ExperienceProperty, out var experienceElement)
            ? experienceElement.GetString()
            : null;

        return new LocalizationSettingsModel(
            language: language,
            theme: theme,
            experience: experience,
            updatedAtUtc: DateTime.UtcNow);
    }
}
