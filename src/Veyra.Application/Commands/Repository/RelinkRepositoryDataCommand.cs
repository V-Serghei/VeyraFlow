using MediatR;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Recovery;

namespace Veyra.Application.Commands.Repository;

public sealed record RelinkRepositoryDataCommand(int RepositoryId)
    : IRequest<OperationResult<RepositoryRecoveryResultDto>>;
