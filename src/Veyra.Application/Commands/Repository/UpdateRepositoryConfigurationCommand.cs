using MediatR;
using Veyra.Application.Common.Results;

namespace Veyra.Application.Commands.Repository;

public sealed record UpdateRepositoryConfigurationCommand(
    int RepositoryId,
    string Name,
    string? Description,
    string DirectoryPath,
    IReadOnlyCollection<string> Formats)
    : IRequest<OperationResult>;
