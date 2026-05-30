using MediatR;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Snapshots;

namespace Veyra.Application.Commands.Repository;

public sealed record RestoreRepositorySnapshotCommand(
    int RepositoryId,
    long SnapshotId,
    string Mode = RepositorySnapshotRestoreMode.Rollback)
    : IRequest<OperationResult<RepositorySnapshotRestoreResultDto>>;
