namespace Veyra.Application.DTOs;

public sealed record RepositoryRetentionPolicyDto(
    bool Enabled,
    int? MaxAgeDays,
    int? MaxSnapshots,
    long? MaxTotalSizeBytes,
    IReadOnlyList<string> TriggerFilters,
    int RunIntervalMinutes,
    DateTime? LastRunAtUtc,
    string? LastStatus);
