using System.Collections.Generic;
using MediatR;

namespace Veyra.Application.Commands.Setup;

public sealed record SetTrackedExtensionsCommand(IReadOnlyCollection<string> Extensions) : IRequest;
