using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Veyra.Desktop.Localization;

public sealed class LocalizationManager : INotifyPropertyChanged
{
    private readonly Dictionary<string, Dictionary<string, string>> _resourcesByLanguage =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, HashSet<string>> _resourceFilesByLanguage =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, HashSet<string>> _duplicateKeysByLanguage =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<LocalizationResourceIssue> _loadIssues = [];

    private readonly LocalizationSettingsStore _settingsStore = new();

    private IReadOnlyDictionary<string, LocalizationLanguageDiagnostics> _diagnosticsByLanguage =
        new ReadOnlyDictionary<string, LocalizationLanguageDiagnostics>(
            new Dictionary<string, LocalizationLanguageDiagnostics>(StringComparer.OrdinalIgnoreCase));

    private string _currentLanguageCode = "en";

    public static LocalizationManager Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public IReadOnlyList<LocalizationLanguageOption> AvailableLanguages { get; private set; } = [];

    public IReadOnlyList<LocalizationResourceIssue> LoadIssues => _loadIssues.AsReadOnly();

    public LocalizationLanguageDiagnostics CurrentDiagnostics => GetDiagnostics(CurrentLanguageCode);

    public string CurrentLanguageCode
    {
        get => _currentLanguageCode;
        private set
        {
            if (string.Equals(_currentLanguageCode, value, StringComparison.OrdinalIgnoreCase))
                return;

            _currentLanguageCode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguageCode)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentDiagnostics)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string this[string key] => Get(key);

    private LocalizationManager()
    {
        LoadResources();

        AvailableLanguages = new ReadOnlyCollection<LocalizationLanguageOption>(
            _resourcesByLanguage.Keys
                .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
                .Select(static code => new LocalizationLanguageOption(code, GetLanguageDisplayName(code)))
                .ToList());

        var preferred = _settingsStore.LoadLanguageCode();
        if (string.IsNullOrWhiteSpace(preferred))
            preferred = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        if (!SetLanguage(preferred, persist: false))
            SetLanguage("en", persist: false);
    }

    public bool SetLanguage(string? languageCode, bool persist = true)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
            return false;

        var normalized = NormalizeLanguageCode(languageCode);
        if (!_resourcesByLanguage.ContainsKey(normalized))
            return false;

        ApplyCulture(normalized);
        CurrentLanguageCode = normalized;

        if (persist)
            _settingsStore.SaveLanguageCode(normalized);

        return true;
    }

    public LocalizationLanguageDiagnostics GetDiagnostics(string? languageCode = null)
    {
        var normalized = NormalizeLanguageCode(languageCode ?? CurrentLanguageCode);
        if (_diagnosticsByLanguage.TryGetValue(normalized, out var diagnostics))
            return diagnostics;

        return LocalizationLanguageDiagnostics.Empty(normalized);
    }

    public string Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        if (_resourcesByLanguage.TryGetValue(CurrentLanguageCode, out var current) &&
            current.TryGetValue(key, out var localized))
            return localized;

        if (_resourcesByLanguage.TryGetValue("en", out var fallback) &&
            fallback.TryGetValue(key, out var english))
            return english;

        return key;
    }

    private void LoadResources()
    {
        _resourcesByLanguage.Clear();
        _resourceFilesByLanguage.Clear();
        _duplicateKeysByLanguage.Clear();
        _loadIssues.Clear();

        var locationRoot = Path.Combine(AppContext.BaseDirectory, "Localization");
        if (!Directory.Exists(locationRoot))
            return;

        var files = Directory.GetFiles(locationRoot, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            var languageCode = GetLanguageCodeFromFile(path);
            if (string.IsNullOrWhiteSpace(languageCode))
            {
                _loadIssues.Add(new LocalizationResourceIssue(
                    LanguageCode: "unknown",
                    FilePath: path,
                    Key: null,
                    Message: "Cannot resolve language code from file name."));
                continue;
            }

            Dictionary<string, string> map;
            try
            {
                var text = File.ReadAllText(path);
                map = JsonSerializer.Deserialize<Dictionary<string, string>>(text)
                      ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _loadIssues.Add(new LocalizationResourceIssue(
                    LanguageCode: languageCode,
                    FilePath: path,
                    Key: null,
                    Message: $"Failed to parse localization file: {ex.Message}"));
                continue;
            }

            if (!_resourcesByLanguage.TryGetValue(languageCode, out var languageMap))
            {
                languageMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _resourcesByLanguage[languageCode] = languageMap;
            }

            if (!_resourceFilesByLanguage.TryGetValue(languageCode, out var languageFiles))
            {
                languageFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _resourceFilesByLanguage[languageCode] = languageFiles;
            }

            languageFiles.Add(Path.GetFileName(path));

            foreach (var (key, value) in map)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (languageMap.ContainsKey(key))
                {
                    if (!_duplicateKeysByLanguage.TryGetValue(languageCode, out var duplicateSet))
                    {
                        duplicateSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        _duplicateKeysByLanguage[languageCode] = duplicateSet;
                    }

                    duplicateSet.Add(key);
                    _loadIssues.Add(new LocalizationResourceIssue(
                        LanguageCode: languageCode,
                        FilePath: path,
                        Key: key,
                        Message: "Duplicate localization key encountered. Last value wins."));
                }

                languageMap[key] = value ?? string.Empty;
            }
        }

        if (!_resourcesByLanguage.ContainsKey("en"))
        {
            _resourcesByLanguage["en"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        BuildDiagnostics();
    }

    private void BuildDiagnostics()
    {
        var englishKeys = _resourcesByLanguage.TryGetValue("en", out var english)
            ? english.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var map = new Dictionary<string, LocalizationLanguageDiagnostics>(StringComparer.OrdinalIgnoreCase);

        foreach (var (languageCode, resources) in _resourcesByLanguage)
        {
            var keys = resources.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingKeys = englishKeys.Except(keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var extraKeys = keys.Except(englishKeys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _duplicateKeysByLanguage.TryGetValue(languageCode, out var duplicateSet);
            _resourceFilesByLanguage.TryGetValue(languageCode, out var filesSet);

            var duplicates = duplicateSet is null
                ? []
                : duplicateSet.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToList();

            map[languageCode] = new LocalizationLanguageDiagnostics(
                LanguageCode: languageCode,
                ResourceFileCount: filesSet?.Count ?? 0,
                TotalKeysCount: keys.Count,
                MissingKeysCount: missingKeys.Count,
                ExtraKeysCount: extraKeys.Count,
                DuplicateKeysCount: duplicates.Count,
                MissingKeysSample: missingKeys.Take(10).ToList(),
                DuplicateKeysSample: duplicates.Take(10).ToList(),
                ExtraKeysSample: extraKeys.Take(10).ToList());
        }

        _diagnosticsByLanguage = new ReadOnlyDictionary<string, LocalizationLanguageDiagnostics>(map);
    }

    private static string? GetLanguageCodeFromFile(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var separator = fileName.IndexOfAny(['.', '-', '_']);
        var rawCode = separator >= 0
            ? fileName[..separator]
            : fileName;

        var normalized = NormalizeLanguageCode(rawCode);
        return normalized.Length >= 2 ? normalized : null;
    }

    private static string NormalizeLanguageCode(string languageCode)
    {
        var code = languageCode.Trim().ToLowerInvariant();
        if (code.Length > 2)
            code = code[..2];

        return code;
    }

    private static void ApplyCulture(string languageCode)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(languageCode);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
        }
        catch
        {
            // Ignore invalid culture fallback.
        }
    }

    private static string GetLanguageDisplayName(string languageCode)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(languageCode);
            return $"{culture.NativeName} ({culture.EnglishName})";
        }
        catch
        {
            return languageCode;
        }
    }
}
