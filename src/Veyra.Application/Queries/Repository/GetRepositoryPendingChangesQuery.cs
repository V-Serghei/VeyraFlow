using MediatR;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.PendingChanges;

namespace Veyra.Application.Queries.Repository;

public sealed record GetRepositoryPendingChangesQuery(
    int RepositoryId,
    int Take = 200)
    : IRequest<RepositoryPendingChangesDto>;
