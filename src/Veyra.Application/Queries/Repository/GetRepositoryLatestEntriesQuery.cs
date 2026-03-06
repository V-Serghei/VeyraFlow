using MediatR;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Repository;

public sealed record GetRepositoryLatestEntriesQuery(int RepositoryId)
    : IRequest<IReadOnlyList<RepositoryScanEntryDto>>;
