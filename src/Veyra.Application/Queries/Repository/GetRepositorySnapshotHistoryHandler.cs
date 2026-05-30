using MediatR;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Snapshots;

namespace Veyra.Application.Queries.Repository;

public sealed class GetRepositorySnapshotHistoryHandler(IRepositorySnapshotRepository snapshots)
    : IRequestHandler<GetRepositorySnapshotHistoryQuery, IReadOnlyList<RepositorySnapshotHistoryItemDto>>
{
    public Task<IReadOnlyList<RepositorySnapshotHistoryItemDto>> Handle(GetRepositorySnapshotHistoryQuery request, CancellationToken ct)
        => snapshots.GetSnapshotHistoryAsync(request.RepositoryId, request.Take, ct);
}
