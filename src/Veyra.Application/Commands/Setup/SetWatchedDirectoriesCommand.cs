using System.Collections.Generic;
using MediatR;

namespace Veyra.Application.Commands.Setup;

public abstract record SetWatchedDirectoriesCommand(IReadOnlyCollection<string> Paths) : IRequest;
