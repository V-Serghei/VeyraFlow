using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Indexing;

public interface ISnapshotComparisonEngine
{
    public Task<SnapshotLinkComparisonResultDto> CompareSnapshotLinksAsync(
        IReadOnlyCollection<SnapshotLinkStateDto> current,
        IReadOnlyCollection<SnapshotLinkStateDto> previous,
        CancellationToken ct = default);

    public Task<RepositoryPathComparisonResultDto> CompareRepositoryPathsAsync(
        IReadOnlyCollection<RepositoryPathStateDto> current,
        IReadOnlyCollection<RepositoryPathStateDto> baseline,
        int take = 2000,
        CancellationToken ct = default);

    public Task<RepositoryVersionPlanningResultDto> PlanRepositoryVersionsAsync(
        IReadOnlyCollection<RepositoryVersionPlanningFileStateDto> states,
        CancellationToken ct = default);
}
