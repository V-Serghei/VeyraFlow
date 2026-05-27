namespace Veyra.Desktop.Services.Monitoring;

public sealed record MonitoringUserSettings(bool Enabled)
{
    public static MonitoringUserSettings Default { get; } = new(false);
}
