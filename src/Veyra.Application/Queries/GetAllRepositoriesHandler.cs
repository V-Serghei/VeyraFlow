using MediatR;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries;

public sealed class GetAllRepositoriesHandler(IRepositoryRepository repo)
    : IRequestHandler<GetAllRepositoriesQuery, IReadOnlyList<RepositoryDto>>
{
    public Task<IReadOnlyList<RepositoryDto>> Handle(GetAllRepositoriesQuery request, CancellationToken ct)
        => repo.GetAllRepositoriesAsync(ct);
}
