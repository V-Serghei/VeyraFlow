using System.Collections.Generic;
using MediatR;

namespace Veyra.Application.Commands.Setup;

public sealed record SyncWatchedSetupCommand(
    IReadOnlyCollection<string> Directories,
    IReadOnlyCollection<string> Extensions) : IRequest;
