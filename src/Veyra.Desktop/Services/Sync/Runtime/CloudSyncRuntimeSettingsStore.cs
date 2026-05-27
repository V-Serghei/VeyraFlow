using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Veyra.Desktop.Services.Sync.Runtime.Models;

namespace Veyra.Desktop.Services.Sync.Runtime;

public sealed class CloudSyncRuntimeSettingsStore : ICloudSyncRuntimeSettingsStore
{
    private readonly string _settingsPath;

    public CloudSyncRuntimeSettingsStore()
    {
        var root = Path.Combine(
            global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow");

        _settingsPath = Path.Combine(root, "runtime-cloud-sync.json");
    }

    public CloudSyncRuntimeUserSettings? Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return null;

            var json = File.ReadAllText(_settingsPath);
            var model = JsonSerializer.Deserialize<CloudSyncRuntimeUserSettings>(json);
            return model is null
                ? null
                : new CloudSyncRuntimeUserSettings(model.IsPaused);
        }
        catch
        {
            return null;
        }
    }

    public Task<CloudSyncRuntimeUserSettings?> LoadAsync(CancellationToken ct = default)
        => Task.FromResult(Load());

    public async Task SaveAsync(CloudSyncRuntimeUserSettings settings, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var normalized = new CloudSyncRuntimeUserSettings(settings.IsPaused);

        await using var stream = new FileStream(
            _settingsPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, normalized, new JsonSerializerOptions { WriteIndented = true }, ct);
    }
}
