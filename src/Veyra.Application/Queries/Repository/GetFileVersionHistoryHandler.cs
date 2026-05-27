using MediatR;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Repository;

public sealed class GetFileVersionHistoryHandler(IRepositorySnapshotRepository snapshots)
    : IRequestHandler<GetFileVersionHistoryQuery, IReadOnlyList<FileVersionInfoDto>>
{
    public Task<IReadOnlyList<FileVersionInfoDto>> Handle(GetFileVersionHistoryQuery request, CancellationToken ct)
        => snapshots.GetFileVersionsAsync(request.RepositoryId, request.RelativePath, request.Take, ct);
}
