using MediatR;
using Veyra.Application.Common.Results;

namespace Veyra.Application.Commands.Repository;

public sealed record RestoreFileVersionCommand(
    int RepositoryId,
    string RelativePath,
    long FileVersionId,
    bool OverwriteCurrent,
    string? TargetPath = null)
    : IRequest<OperationResult<string>>;
