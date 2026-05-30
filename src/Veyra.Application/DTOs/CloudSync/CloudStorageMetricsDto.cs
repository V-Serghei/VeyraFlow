namespace Veyra.Application.DTOs.CloudSync;

public sealed record CloudStorageMetricsDto(
    bool Ok,
    CloudStorageSummaryDto Summary,
    CloudStorageBlockMetricsDto Blocks,
    CloudStoragePackMetricsDto Packs,
    CloudStorageFilesystemStatsDto Filesystem);
