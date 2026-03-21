using System.Collections.Generic;

namespace Veyra.Application.DTOs;

public sealed record CloudPushResultDto(
    bool Ok,
    IReadOnlyList<string> MissingBlockHashes);
