using MediatR;
using Veyra.Application.Common.Results;

namespace Veyra.Application.Commands.Setup;

public sealed record SaveInitialSetupCommand(
    IReadOnlyCollection<string> Directories,
    IReadOnlyCollection<string> Extensions,
    string RepositoryName) : IRequest<OperationResult>;
