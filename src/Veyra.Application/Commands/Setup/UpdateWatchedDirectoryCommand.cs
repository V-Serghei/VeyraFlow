using MediatR;

namespace Veyra.Application.Commands.Setup;

public abstract record UpdateWatchedDirectoryCommand(string OldPath, string NewPath) : IRequest;
