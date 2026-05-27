using MediatR;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Repository;

public sealed record GetRepositoryPendingChangesQuery(
    int RepositoryId,
    int Take = 200)
    : IRequest<RepositoryPendingChangesDto>;
