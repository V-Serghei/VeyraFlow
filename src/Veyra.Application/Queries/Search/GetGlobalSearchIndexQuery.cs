using MediatR;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Search;

public sealed record GetGlobalSearchIndexQuery(int SnapshotTake = 60)
    : IRequest<GlobalSearchIndexLoadDto>;
