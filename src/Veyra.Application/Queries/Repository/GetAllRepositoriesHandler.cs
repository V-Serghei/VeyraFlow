using MediatR;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Core;

namespace Veyra.Application.Queries.Repository;

public sealed class GetAllRepositoriesHandler(IRepositoryRepository repo)
    : IRequestHandler<GetAllRepositoriesQuery, IReadOnlyList<RepositoryDto>>
{
    public Task<IReadOnlyList<RepositoryDto>> Handle(GetAllRepositoriesQuery request, CancellationToken ct)
        => repo.GetAllRepositoriesAsync(ct);
}
