namespace Veyra.Application.DTOs.CloudSync;

public sealed record CloudStorageSummaryDto(
    long LogicalBlockCount,
    long LogicalBytes,
    long PhysicalObjectCount,
    long PhysicalPayloadBytes,
    long MissingBlockCount,
    long ReducedObjectCount,
    long ReducedObjectPercentFloor);
