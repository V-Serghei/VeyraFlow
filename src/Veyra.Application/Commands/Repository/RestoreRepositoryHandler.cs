using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;

namespace Veyra.Application.Commands.Repository;
public sealed class RestoreRepositoryHandler(
    IRepositoryRepository repo,
    ILogger<RestoreRepositoryHandler> log)
    : IRequestHandler<RestoreRepositoryCommand>
{
    public async Task Handle(RestoreRepositoryCommand request, CancellationToken ct)
    {
        await repo.RestoreRepositoryAsync(request.Id, ct);
        log.LogInformation("Restored repository {Id}", request.Id);
    }
}
