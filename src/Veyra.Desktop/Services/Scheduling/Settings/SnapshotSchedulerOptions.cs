namespace Veyra.Desktop.Services.Scheduling;

public sealed class SnapshotSchedulerOptions
{
    public const int RecommendedPollSeconds = 60;
    public const int RecommendedIntervalMinutes = 60;
    public const int RecommendedMaxReadBytesPerSecond = 16 * 1024 * 1024;
    public const int RecommendedMaxIoOperationsPerSecond = 128;
    public const int RecommendedIntegrityIntervalMinutes = 12 * 60;

    public bool Enabled { get; set; } = true;
    public int PollSeconds { get; set; } = RecommendedPollSeconds;
    public int IntervalMinutes { get; set; } = RecommendedIntervalMinutes;
    public int QuietHoursStartHour { get; set; } = 0;
    public int QuietHoursEndHour { get; set; } = 0;
    public int MaxConcurrentScans { get; set; } = 2;
    public int MaxReadBytesPerSecond { get; set; } = RecommendedMaxReadBytesPerSecond;
    public int MaxIoOperationsPerSecond { get; set; } = RecommendedMaxIoOperationsPerSecond;
    public int RetryCount { get; set; } = 2;
    public int RetryDelaySeconds { get; set; } = 10;

    public bool IntegrityEnabled { get; set; } = true;
    public int IntegrityIntervalMinutes { get; set; } = RecommendedIntegrityIntervalMinutes;
    public bool IntegrityRepairFromCloud { get; set; } = false;
    public int IntegrityIssueSampleLimit { get; set; } = 200;
}
