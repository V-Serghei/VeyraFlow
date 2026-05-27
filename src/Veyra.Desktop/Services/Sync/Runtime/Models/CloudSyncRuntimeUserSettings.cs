namespace Veyra.Desktop.Services.Sync.Runtime.Models;

public sealed record CloudSyncRuntimeUserSettings(bool IsPaused)
{
    public static CloudSyncRuntimeUserSettings Default { get; } = new(false);
}
