using MediatR;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Scanning;

namespace Veyra.Application.Queries.Repository;

public sealed record GetRepositoryLatestEntriesQuery(int RepositoryId)
    : IRequest<IReadOnlyList<RepositoryScanEntryDto>>;
