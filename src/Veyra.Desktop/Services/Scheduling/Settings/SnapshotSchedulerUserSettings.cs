using System;

namespace Veyra.Desktop.Services.Scheduling;

public sealed record SnapshotSchedulerUserSettings(
    bool Enabled,
    int IntervalMinutes,
    int QuietHoursStartHour,
    int QuietHoursEndHour,
    int? PollSeconds = null,
    int? MaxReadBytesPerSecond = null,
    int? MaxIoOperationsPerSecond = null,
    bool? IntegrityEnabled = null,
    int? IntegrityIntervalMinutes = null);
