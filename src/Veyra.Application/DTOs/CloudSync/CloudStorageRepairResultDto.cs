namespace Veyra.Application.DTOs.CloudSync;

public sealed record CloudStorageRepairResultDto(
    bool Ok,
    CloudStorageRepairStatsDto Repair,
    CloudStorageMetricsDto Metrics);
