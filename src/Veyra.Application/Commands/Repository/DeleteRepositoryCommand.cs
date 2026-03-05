using MediatR;

namespace Veyra.Application.Commands.Repository;

public sealed record DeleteRepositoryCommand(int Id) : IRequest;
