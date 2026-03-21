using MediatR;
using Veyra.Application.Abstractions.Setup;

namespace Veyra.Application.Queries.Repository;

public sealed class GetTrackedExtensionsHandler(ISetupRepository repo)
    : IRequestHandler<GetTrackedExtensionsQuery, List<string>>
{
    public async Task<List<string>> Handle(GetTrackedExtensionsQuery request, CancellationToken ct)
    {
        var result = await repo.GetTrackedExtensionsAsync(ct);
        return result.ToList();
    }
}
