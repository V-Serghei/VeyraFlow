using MediatR;

namespace Veyra.Application.Commands.Repository;

public sealed record RestoreRepositoryCommand(int Id) : IRequest;
