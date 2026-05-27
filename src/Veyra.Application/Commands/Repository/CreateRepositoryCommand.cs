using MediatR;
using Veyra.Application.Common.Results;

namespace Veyra.Application.Commands.Repository;

public sealed record CreateRepositoryCommand(string Name, string? Description, string DirectoryPath) : IRequest<OperationResult<int>>;
