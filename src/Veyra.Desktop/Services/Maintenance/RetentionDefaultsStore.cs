using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Maintenance;

public sealed class RetentionDefaultsStore : IRetentionDefaultsStore
{
    private readonly string _settingsPath;

    public RetentionDefaultsStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow");

        _settingsPath = Path.Combine(root, "retention-defaults.json");
    }

    public RetentionDefaultsUserSettings? Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return null;

            var json = File.ReadAllText(_settingsPath);
            var model = JsonSerializer.Deserialize<RetentionDefaultsUserSettings>(json);
            if (model is null)
                return null;

            return new RetentionDefaultsUserSettings(
                model.Enabled,
                model.MaxAgeDays,
                model.MaxSnapshots,
                model.MaxTotalSizeBytes,
                model.TriggerFilter,
                Math.Clamp(model.RunIntervalMinutes, 5, 7 * 24 * 60));
        }
        catch
        {
            return null;
        }
    }

    public Task<RetentionDefaultsUserSettings?> LoadAsync(CancellationToken ct = default)
        => Task.FromResult(Load());

    public async Task SaveAsync(RetentionDefaultsUserSettings settings, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var normalized = new RetentionDefaultsUserSettings(
            settings.Enabled,
            settings.MaxAgeDays,
            settings.MaxSnapshots,
            settings.MaxTotalSizeBytes,
            string.IsNullOrWhiteSpace(settings.TriggerFilter) ? null : settings.TriggerFilter.Trim(),
            Math.Clamp(settings.RunIntervalMinutes, 5, 7 * 24 * 60));

        await using var stream = new FileStream(_settingsPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, normalized, new JsonSerializerOptions { WriteIndented = true }, ct);
    }
}
