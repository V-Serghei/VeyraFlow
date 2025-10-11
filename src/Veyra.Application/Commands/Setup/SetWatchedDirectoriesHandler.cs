using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;

namespace Veyra.Application.Commands.Setup;

public sealed class SetWatchedDirectoriesHandler : IRequestHandler<SetWatchedDirectoriesCommand>
{
    private readonly ISetupState _state;
    private readonly ILogger<SetWatchedDirectoriesHandler> _log;

    public SetWatchedDirectoriesHandler(ISetupState state, ILogger<SetWatchedDirectoriesHandler> log)
    {
        _state = state;
        _log = log;
    }

    public Task Handle(SetWatchedDirectoriesCommand request, CancellationToken cancellationToken)
    {
        _state.SetWatchedDirectories(request.Paths);
        _log.LogInformation("Watched directories set: {Count}", request.Paths.Count);

        // TODO(native): call validation
        // TODO(rust):

        return Task.CompletedTask;
    }
}
