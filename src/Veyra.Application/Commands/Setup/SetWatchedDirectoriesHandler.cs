using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;

namespace Veyra.Application.Commands.Setup;

public sealed class SetWatchedDirectoriesHandler(
    ISetupRepository repo,
    INativeSetupApplier native,
    ILogger<SetWatchedDirectoriesHandler> log)
    : IRequestHandler<SetWatchedDirectoriesCommand>
{
    public async Task Handle(SetWatchedDirectoriesCommand request, CancellationToken cancellationToken)
    {
        await repo.ReplaceWatchedDirectoriesAsync(request.Paths, cancellationToken);
        log.LogInformation("Watched directories saved: {Count}", request.Paths.Count);
        await native.ApplySetupAsync(cancellationToken);
    }
}
