using MediatR;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Search;

namespace Veyra.Application.Queries.Search;

public sealed record GetGlobalSearchIndexQuery(int SnapshotTake = 60)
    : IRequest<GlobalSearchIndexLoadDto>;
