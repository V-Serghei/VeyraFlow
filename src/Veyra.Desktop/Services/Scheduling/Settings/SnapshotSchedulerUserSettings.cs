using System;

namespace Veyra.Desktop.Services.Scheduling;

public sealed record SnapshotSchedulerUserSettings(
    bool Enabled,
    int IntervalMinutes,
    int QuietHoursStartHour,
    int QuietHoursEndHour);
