namespace Veyra.Desktop.Services.Scheduling;

public sealed class SnapshotSchedulerOptions
{
    public bool Enabled { get; init; } = true;
    public int PollSeconds { get; init; } = 30;
    public int IntervalMinutes { get; init; } = 15;
    public int QuietHoursStartHour { get; init; } = 0;
    public int QuietHoursEndHour { get; init; } = 0;
    public int MaxReadBytesPerSecond { get; init; } = 0;
    public int MaxIoOperationsPerSecond { get; init; } = 0;
    public int RetryCount { get; init; } = 2;
    public int RetryDelaySeconds { get; init; } = 10;
}

