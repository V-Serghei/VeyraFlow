using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;

namespace Veyra.Application.Commands.Setup;

public sealed class SetTrackedExtensionsHandler : IRequestHandler<SetTrackedExtensionsCommand>
{
    private readonly ISetupState _state;
    private readonly ILogger<SetTrackedExtensionsHandler> _log;

    public SetTrackedExtensionsHandler(ISetupState state, ILogger<SetTrackedExtensionsHandler> log)
    {
        _state = state;
        _log = log;
    }

    public Task Handle(SetTrackedExtensionsCommand request, CancellationToken cancellationToken)
    {
        _state.SetTrackedExtensions(request.Extensions);
        _log.LogInformation("Tracked extensions set: {Count}", request.Extensions.Count);

        // TODO(native): transfer config ignore
        // TODO(rust): config filter

        return Task.CompletedTask;
    }
}
