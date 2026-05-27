using System.Collections.Generic;
using MediatR;

namespace Veyra.Application.Commands.Setup;

public sealed record SetWatchedDirectoriesCommand(IReadOnlyCollection<string> Paths) : IRequest;
