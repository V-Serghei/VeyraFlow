using MediatR;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Snapshots;

namespace Veyra.Application.Queries.Repository;

public sealed record GetRepositorySnapshotRestorePlanQuery(
    int RepositoryId,
    long SnapshotId)
    : IRequest<OperationResult<RepositorySnapshotRestorePlanDto>>;
