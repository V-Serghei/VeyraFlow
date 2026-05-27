using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Monitoring;

public sealed class MonitoringSettingsStore : IMonitoringSettingsStore
{
    private readonly string _settingsPath;

    public MonitoringSettingsStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow");

        _settingsPath = Path.Combine(root, "monitoring-settings.json");
    }

    public MonitoringUserSettings? Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return null;

            var json = File.ReadAllText(_settingsPath);
            var model = JsonSerializer.Deserialize<MonitoringUserSettings>(json);
            return model is null
                ? null
                : new MonitoringUserSettings(model.Enabled);
        }
        catch
        {
            return null;
        }
    }

    public Task<MonitoringUserSettings?> LoadAsync(CancellationToken ct = default)
        => Task.FromResult(Load());

    public async Task SaveAsync(MonitoringUserSettings settings, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var stream = new FileStream(
            _settingsPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous);

        await JsonSerializer.SerializeAsync(
            stream,
            new MonitoringUserSettings(settings.Enabled),
            new JsonSerializerOptions { WriteIndented = true },
            ct);
    }
}
