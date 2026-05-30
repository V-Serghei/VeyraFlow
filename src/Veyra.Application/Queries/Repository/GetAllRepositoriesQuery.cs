using MediatR;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Core;

namespace Veyra.Application.Queries.Repository;

public sealed record GetAllRepositoriesQuery() : IRequest<IReadOnlyList<RepositoryDto>>;
