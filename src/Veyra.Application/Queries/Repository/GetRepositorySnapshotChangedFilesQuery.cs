using MediatR;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Snapshots;

namespace Veyra.Application.Queries.Repository;

public sealed record GetRepositorySnapshotChangedFilesQuery(
    int RepositoryId,
    long SnapshotId,
    int Take = 1000)
    : IRequest<IReadOnlyList<RepositorySnapshotFileChangeDto>>;
