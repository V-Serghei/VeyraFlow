using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;

namespace Veyra.Application.Commands.Repository;

public sealed class DeleteRepositoryHandler(
    IRepositoryRepository repo,
    ILogger<DeleteRepositoryHandler> log)
    : IRequestHandler<DeleteRepositoryCommand>
{
    public async Task Handle(DeleteRepositoryCommand request, CancellationToken ct)
    {
        await repo.DeleteRepositoryAsync(request.Id, ct);
        log.LogInformation("Soft-deleted repository {Id}", request.Id);
    }
}
