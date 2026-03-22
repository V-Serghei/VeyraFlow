namespace Veyra.Desktop.Services.Scheduling;

public sealed class SnapshotSchedulerOptions
{
    public bool Enabled { get; set; } = true;
    public int PollSeconds { get; set; } = 30;
    public int IntervalMinutes { get; set; } = 15;
    public int QuietHoursStartHour { get; set; } = 0;
    public int QuietHoursEndHour { get; set; } = 0;
    public int MaxConcurrentScans { get; set; } = 2;
    public int MaxReadBytesPerSecond { get; set; } = 0;
    public int MaxIoOperationsPerSecond { get; set; } = 0;
    public int RetryCount { get; set; } = 2;
    public int RetryDelaySeconds { get; set; } = 10;

    public bool IntegrityEnabled { get; set; } = true;
    public int IntegrityIntervalMinutes { get; set; } = 180;
    public bool IntegrityRepairFromCloud { get; set; } = false;
    public int IntegrityIssueSampleLimit { get; set; } = 200;
}
