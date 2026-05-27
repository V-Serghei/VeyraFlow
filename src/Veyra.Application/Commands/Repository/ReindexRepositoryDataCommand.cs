using MediatR;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Commands.Repository;

public sealed record ReindexRepositoryDataCommand(int RepositoryId)
    : IRequest<OperationResult<RepositoryRecoveryResultDto>>;
