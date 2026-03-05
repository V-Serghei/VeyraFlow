using MediatR;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries;

public sealed record GetAllRepositoriesQuery() : IRequest<IReadOnlyList<RepositoryDto>>;
