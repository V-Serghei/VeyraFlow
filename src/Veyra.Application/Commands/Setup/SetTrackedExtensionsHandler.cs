using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;

namespace Veyra.Application.Commands.Setup;

public sealed class SetTrackedExtensionsHandler(
    ISetupRepository repo,
    INativeSetupApplier native,
    ILogger<SetTrackedExtensionsHandler> log)
    : IRequestHandler<SetTrackedExtensionsCommand>
{
    public async Task Handle(SetTrackedExtensionsCommand request, CancellationToken cancellationToken)
    {
        await repo.ReplaceTrackedExtensionsAsync(request.Extensions, cancellationToken);
        log.LogInformation("Tracked extensions saved: {Count}", request.Extensions.Count);
        await native.ApplySetupAsync(cancellationToken);
    }
}
