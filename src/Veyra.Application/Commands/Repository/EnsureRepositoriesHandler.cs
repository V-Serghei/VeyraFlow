using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;

namespace Veyra.Application.Commands.Repository;

public sealed class EnsureRepositoriesHandler(
    IRepositoryRepository repo,
    ILogger<EnsureRepositoriesHandler> log)
    : IRequestHandler<EnsureRepositoriesCommand>
{
    public async Task Handle(EnsureRepositoriesCommand request, CancellationToken ct)
    {
        await repo.EnsureRepositoriesForAllDirectoriesAsync(ct);
        log.LogInformation("Ensured repositories for all watched directories");
    }
}
