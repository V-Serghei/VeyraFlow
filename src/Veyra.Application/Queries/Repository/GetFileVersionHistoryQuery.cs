using MediatR;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.FileVersions;

namespace Veyra.Application.Queries.Repository;

public sealed record GetFileVersionHistoryQuery(
    int RepositoryId,
    string RelativePath,
    int Take = 50)
    : IRequest<IReadOnlyList<FileVersionInfoDto>>;
