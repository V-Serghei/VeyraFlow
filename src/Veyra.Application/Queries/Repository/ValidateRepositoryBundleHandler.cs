using MediatR;
using Veyra.Application.Abstractions.Setup;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Repository;

public sealed class ValidateRepositoryBundleHandler(IRepositoryBundleService bundles)
    : IRequestHandler<ValidateRepositoryBundleQuery, RepositoryBundleValidationResultDto>
{
    public async Task<RepositoryBundleValidationResultDto> Handle(
        ValidateRepositoryBundleQuery request,
        CancellationToken ct)
        => await bundles.ValidateBundleAsync(request.BundlePath, ct);
}
