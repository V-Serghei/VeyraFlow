using System;
using System.IO;
using System.Text.Json;

namespace Veyra.Desktop.Localization;

internal sealed class LocalizationSettingsStore
{
    private const string LanguageProperty = "language";

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
            if (!File.Exists(_settingsPath))
                return null;

            using var stream = File.OpenRead(_settingsPath);
            using var document = JsonDocument.Parse(stream);

            if (!document.RootElement.TryGetProperty(LanguageProperty, out var languageElement))
                return null;

            return languageElement.GetString();
        }
        catch
        {
            return null;
        }
    }

    public void SaveLanguageCode(string languageCode)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var model = new SettingsModel(languageCode, DateTime.UtcNow);
            var json = JsonSerializer.Serialize(model, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // Ignore persistence errors: localization fallback will still work.
        }
    }

    private sealed record SettingsModel(string language, DateTime updatedAtUtc);
}
