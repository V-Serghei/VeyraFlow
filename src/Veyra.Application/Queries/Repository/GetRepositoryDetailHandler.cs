using MediatR;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Core;

namespace Veyra.Application.Queries.Repository;

public sealed class GetRepositoryDetailHandler(IRepositoryRepository repo)
    : IRequestHandler<GetRepositoryDetailQuery, RepositoryDto?>
{
    public Task<RepositoryDto?> Handle(GetRepositoryDetailQuery request, CancellationToken ct)
        => repo.GetRepositoryByIdAsync(request.Id, ct);
}
