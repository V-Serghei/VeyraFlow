using MediatR;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Repository;

public sealed record GetRepositorySnapshotChangedFilesQuery(
    int RepositoryId,
    long SnapshotId,
    int Take = 1000)
    : IRequest<IReadOnlyList<RepositorySnapshotFileChangeDto>>;
