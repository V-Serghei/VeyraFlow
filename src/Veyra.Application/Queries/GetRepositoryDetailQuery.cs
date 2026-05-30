using MediatR;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Core;

namespace Veyra.Application.Queries;

public sealed record GetRepositoryDetailQuery(int Id) : IRequest<RepositoryDto?>;
