using MediatR;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Scanning;

namespace Veyra.Application.Commands.Repository;

public sealed record ScanRepositoryCommand(
    int RepositoryId,
    IProgress<RepositoryScanProgressDto>? Progress = null,
    RepositoryScanOptionsDto? Options = null)
    : IRequest<OperationResult<RepositoryScanResultDto>>;

