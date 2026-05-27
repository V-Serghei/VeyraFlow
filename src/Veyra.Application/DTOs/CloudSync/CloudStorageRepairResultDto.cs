namespace Veyra.Application.DTOs;

public sealed record CloudStorageRepairResultDto(
    bool Ok,
    CloudStorageRepairStatsDto Repair,
    CloudStorageMetricsDto Metrics);
