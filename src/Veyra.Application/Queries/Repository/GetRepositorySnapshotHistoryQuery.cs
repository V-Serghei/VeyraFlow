using MediatR;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Repository;

public sealed record GetRepositorySnapshotHistoryQuery(
    int RepositoryId,
    int Take = 100)
    : IRequest<IReadOnlyList<RepositorySnapshotHistoryItemDto>>;
