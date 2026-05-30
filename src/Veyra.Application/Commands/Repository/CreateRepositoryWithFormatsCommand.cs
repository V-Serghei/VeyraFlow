using MediatR;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Core;

namespace Veyra.Application.Commands.Repository;

public sealed record CreateRepositoryWithFormatsCommand(
    string Name,
    string? Description,
    string DirectoryPath,
    IReadOnlyCollection<string> Formats,
    IProgress<RepositoryCreationProgressDto>? Progress = null)
    : IRequest<OperationResult<RepositoryCreationOutcomeDto>>;
