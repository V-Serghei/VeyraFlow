using MediatR;

namespace Veyra.Application.Commands.Repository;

public sealed record UpdateRepositoryCommand(int Id, string Name, string? Description) : IRequest;
