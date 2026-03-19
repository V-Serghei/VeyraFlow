using System;
using Veyra.Application.DTOs;

namespace Veyra.Desktop.Services.Maintenance;

public sealed record RetentionDefaultsUserSettings(
    bool Enabled,
    int? MaxAgeDays,
    int? MaxSnapshots,
    long? MaxTotalSizeBytes,
    string? TriggerFilter,
    int RunIntervalMinutes)
{
    public RepositoryRetentionPolicyDto ToPolicy() => new(
        Enabled,
        MaxAgeDays,
        MaxSnapshots,
        MaxTotalSizeBytes,
        string.IsNullOrWhiteSpace(TriggerFilter) ? [] : TriggerFilter.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        RunIntervalMinutes,
        null,
        null);
}
