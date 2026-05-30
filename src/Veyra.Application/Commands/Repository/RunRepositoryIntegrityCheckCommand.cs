using MediatR;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Integrity;

namespace Veyra.Application.Commands.Repository;

public abstract record RunRepositoryIntegrityCheckCommand(
    int RepositoryId,
    bool RepairFromCloud = false,
    int MaxIssueSamples = 200,
    IProgress<RepositoryIntegrityProgressDto>? Progress = null)
    : IRequest<OperationResult<RepositoryIntegrityRunResultDto>>;
