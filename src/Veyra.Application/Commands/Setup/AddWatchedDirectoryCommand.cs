using MediatR;

namespace Veyra.Application.Commands.Setup;

public sealed record AddWatchedDirectoryCommand(string Path) : IRequest;
