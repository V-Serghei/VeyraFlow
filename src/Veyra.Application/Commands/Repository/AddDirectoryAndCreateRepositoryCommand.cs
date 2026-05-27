using MediatR;
using Veyra.Application.Common.Results;
namespace Veyra.Application.Commands.Repository;

public sealed record AddDirectoryAndCreateRepositoryCommand(string DirectoryPath, string? RepositoryName) : IRequest<OperationResult<int>>;
