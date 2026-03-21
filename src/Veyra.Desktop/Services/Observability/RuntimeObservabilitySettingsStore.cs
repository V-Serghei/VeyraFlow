using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Observability;

public sealed class RuntimeObservabilitySettingsStore : IRuntimeObservabilitySettingsStore
{
    private readonly string _settingsPath;

    public RuntimeObservabilitySettingsStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow");

        _settingsPath = Path.Combine(root, "runtime-observability.json");
    }

    public RuntimeObservabilityUserSettings? Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return null;

            var json = File.ReadAllText(_settingsPath);
            var model = JsonSerializer.Deserialize<RuntimeObservabilityUserSettings>(json);
            if (model is null)
                return null;

            return new RuntimeObservabilityUserSettings(
                DiagnosticsEnabled: model.DiagnosticsEnabled,
                LoggingEnabled: model.LoggingEnabled);
        }
        catch
        {
            return null;
        }
    }

    public Task<RuntimeObservabilityUserSettings?> LoadAsync(CancellationToken ct = default)
        => Task.FromResult(Load());

    public async Task SaveAsync(RuntimeObservabilityUserSettings settings, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var normalized = new RuntimeObservabilityUserSettings(
            DiagnosticsEnabled: settings.DiagnosticsEnabled,
            LoggingEnabled: settings.LoggingEnabled);

        await using var stream = new FileStream(_settingsPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, normalized, new JsonSerializerOptions { WriteIndented = true }, ct);
    }
}
