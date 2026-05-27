using MediatR;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Repository;

public sealed class GetRepositorySnapshotChangedFilesHandler(IRepositorySnapshotRepository snapshots)
    : IRequestHandler<GetRepositorySnapshotChangedFilesQuery, IReadOnlyList<RepositorySnapshotFileChangeDto>>
{
    public Task<IReadOnlyList<RepositorySnapshotFileChangeDto>> Handle(GetRepositorySnapshotChangedFilesQuery request, CancellationToken ct)
        => snapshots.GetSnapshotChangedFilesAsync(request.RepositoryId, request.SnapshotId, request.Take, ct);
}
