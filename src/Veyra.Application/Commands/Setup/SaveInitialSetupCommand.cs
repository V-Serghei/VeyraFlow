using MediatR;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Commands.Setup;

public sealed record SaveInitialSetupCommand(
    IReadOnlyCollection<string> Directories,
    IReadOnlyCollection<string> Extensions,
    string RepositoryName,
    IProgress<RepositoryCreationProgressDto>? Progress = null) : IRequest<OperationResult>;
