using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Scheduling;

public sealed class SnapshotSchedulerSettingsStore : ISnapshotSchedulerSettingsStore
{
    private readonly string _settingsPath;

    public SnapshotSchedulerSettingsStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow");

        _settingsPath = Path.Combine(root, "scheduler-settings.json");
    }

    public SnapshotSchedulerUserSettings? Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return null;

            var json = File.ReadAllText(_settingsPath);
            var model = JsonSerializer.Deserialize<SnapshotSchedulerUserSettings>(json);
            if (model is null)
                return null;

            return new SnapshotSchedulerUserSettings(
                model.Enabled,
                Math.Clamp(model.IntervalMinutes, 1, 24 * 60),
                Math.Clamp(model.QuietHoursStartHour, 0, 23),
                Math.Clamp(model.QuietHoursEndHour, 0, 23));
        }
        catch
        {
            return null;
        }
    }

    public Task<SnapshotSchedulerUserSettings?> LoadAsync(CancellationToken ct = default)
        => Task.FromResult(Load());

    public async Task SaveAsync(SnapshotSchedulerUserSettings settings, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var normalized = new SnapshotSchedulerUserSettings(
            settings.Enabled,
            Math.Clamp(settings.IntervalMinutes, 1, 24 * 60),
            Math.Clamp(settings.QuietHoursStartHour, 0, 23),
            Math.Clamp(settings.QuietHoursEndHour, 0, 23));

        await using var stream = new FileStream(_settingsPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, normalized, new JsonSerializerOptions { WriteIndented = true }, ct);
    }
}
