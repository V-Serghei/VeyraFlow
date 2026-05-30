using MediatR;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Bundles;

namespace Veyra.Application.Queries.Repository;

public sealed record ValidateRepositoryBundleQuery(string BundlePath)
    : IRequest<RepositoryBundleValidationResultDto>;
