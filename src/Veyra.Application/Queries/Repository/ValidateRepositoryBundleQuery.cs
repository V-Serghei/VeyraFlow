using MediatR;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Repository;

public sealed record ValidateRepositoryBundleQuery(string BundlePath)
    : IRequest<RepositoryBundleValidationResultDto>;
