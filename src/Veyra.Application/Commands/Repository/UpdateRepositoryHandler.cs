using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;

namespace Veyra.Application.Commands.Repository;

public sealed class UpdateRepositoryHandler(
    IRepositoryRepository repo,
    ILogger<UpdateRepositoryHandler> log)
    : IRequestHandler<UpdateRepositoryCommand>
{
    public async Task Handle(UpdateRepositoryCommand request, CancellationToken ct)
    {
        await repo.UpdateRepositoryAsync(request.Id, request.Name, request.Description, ct);
        log.LogInformation("Updated repository {Id}: {Name}", request.Id, request.Name);
    }
}
