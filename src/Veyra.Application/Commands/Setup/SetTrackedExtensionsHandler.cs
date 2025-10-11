using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;

namespace Veyra.Application.Commands.Setup;

public sealed class SetTrackedExtensionsHandler(ISetupState state, ILogger<SetTrackedExtensionsHandler> log)
    : IRequestHandler<SetTrackedExtensionsCommand>
{
    public Task Handle(SetTrackedExtensionsCommand request, CancellationToken cancellationToken)
    {
        state.SetTrackedExtensions(request.Extensions);
        log.LogInformation("Tracked extensions set: {Count}", request.Extensions.Count);

        // TODO(native): transfer config ignore
        // TODO(rust): config filter

        return Task.CompletedTask;
    }
}
