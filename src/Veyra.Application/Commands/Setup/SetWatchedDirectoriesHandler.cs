using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;

namespace Veyra.Application.Commands.Setup;

public sealed class SetWatchedDirectoriesHandler(ISetupState state, ILogger<SetWatchedDirectoriesHandler> log)
    : IRequestHandler<SetWatchedDirectoriesCommand>
{
    public Task Handle(SetWatchedDirectoriesCommand request, CancellationToken cancellationToken)
    {
        state.SetWatchedDirectories(request.Paths);
        log.LogInformation("Watched directories set: {Count}", request.Paths.Count);

        // TODO(native): call validation
        // TODO(rust):

        return Task.CompletedTask;
    }
}
