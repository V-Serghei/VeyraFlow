using MediatR;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Repository.Bundles;

namespace Veyra.Application.Commands.Repository;

public sealed record ImportRepositoryBundleCommand(
    string BundlePath,
    string TargetDirectoryPath,
    string? RepositoryNameOverride = null)
    : IRequest<OperationResult<RepositoryBundleImportResultDto>>;
