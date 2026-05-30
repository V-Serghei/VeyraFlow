using MediatR;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Retention;

namespace Veyra.Application.Commands.Repository;

public sealed record RunRepositoryRetentionCommand(
    int RepositoryId,
    bool DryRun,
    RepositoryRetentionPolicyDto? PolicyOverride = null,
    IProgress<RepositoryRetentionProgressDto>? Progress = null)
    : IRequest<OperationResult<RepositoryRetentionRunResultDto>>;
