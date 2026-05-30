using MediatR;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.PendingChanges;

namespace Veyra.Application.Queries.Repository;

public sealed class GetRepositoryPendingChangesHandler(IRepositorySnapshotRepository snapshots)
    : IRequestHandler<GetRepositoryPendingChangesQuery, RepositoryPendingChangesDto>
{
    public Task<RepositoryPendingChangesDto> Handle(GetRepositoryPendingChangesQuery request, CancellationToken ct)
        => snapshots.GetPendingChangesAsync(request.RepositoryId, request.Take, ct);
}
