using MediatR;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Repository;

public sealed record GetAllRepositoriesQuery() : IRequest<IReadOnlyList<RepositoryDto>>;
