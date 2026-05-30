namespace Veyra.Application.DTOs.CloudSync;

public sealed record CloudPushResultDto(
    bool Ok,
    IReadOnlyList<string> MissingBlockHashes);
