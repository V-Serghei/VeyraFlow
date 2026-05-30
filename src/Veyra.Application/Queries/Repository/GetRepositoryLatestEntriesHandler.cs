using MediatR;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Scanning;

namespace Veyra.Application.Queries.Repository;

public sealed class GetRepositoryLatestEntriesHandler(IRepositorySnapshotRepository snapshots)
    : IRequestHandler<GetRepositoryLatestEntriesQuery, IReadOnlyList<RepositoryScanEntryDto>>
{
    public Task<IReadOnlyList<RepositoryScanEntryDto>> Handle(GetRepositoryLatestEntriesQuery request, CancellationToken ct)
        => snapshots.GetLatestEntriesAsync(request.RepositoryId, ct);
}
