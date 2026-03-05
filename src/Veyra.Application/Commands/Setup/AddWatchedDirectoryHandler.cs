﻿using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Setup;

namespace Veyra.Application.Commands.Setup;

public sealed class AddWatchedDirectoryHandler(
    ISetupRepository repo,
    INativeSetupApplier native,
    ILogger<AddWatchedDirectoryHandler> log)
    : IRequestHandler<AddWatchedDirectoryCommand>
{
    public async Task Handle(AddWatchedDirectoryCommand request, CancellationToken cancellationToken)
    {
        await repo.AddWatchedDirectoryAsync(request.Path, cancellationToken);
        log.LogInformation("Added watched directory: {Path}", request.Path);
        await native.ApplySetupAsync(cancellationToken);
    }
}
