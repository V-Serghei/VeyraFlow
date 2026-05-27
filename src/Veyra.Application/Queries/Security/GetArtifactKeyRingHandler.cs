using MediatR;
using Veyra.Application.Abstractions.Security;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Security;

public sealed class GetArtifactKeyRingHandler(IArtifactKeyManagementService keyManagement)
    : IRequestHandler<GetArtifactKeyRingQuery, ArtifactKeyRingStateDto>
{
    public async Task<ArtifactKeyRingStateDto> Handle(GetArtifactKeyRingQuery request, CancellationToken ct)
        => await keyManagement.GetKeyRingAsync(ct);
}
