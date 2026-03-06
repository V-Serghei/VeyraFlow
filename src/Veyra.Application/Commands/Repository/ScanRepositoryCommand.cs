using MediatR;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Commands.Repository;

public sealed record ScanRepositoryCommand(
    int RepositoryId,
    IProgress<RepositoryScanProgressDto>? Progress = null)
    : IRequest<OperationResult<RepositoryScanResultDto>>;
