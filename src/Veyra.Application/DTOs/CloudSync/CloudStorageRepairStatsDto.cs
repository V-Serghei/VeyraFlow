namespace Veyra.Application.DTOs;

public sealed record CloudStorageRepairStatsDto(
    int Scanned,
    int MissingMarked,
    int BrokenLooseRefs,
    int BrokenPackRefs,
    int Compacted);
