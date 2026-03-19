namespace Veyra.Infrastructure.Data.Preview;

public sealed record SvgStructuralDiffSummary(
    int AddedElementCount,
    int RemovedElementCount,
    int ModifiedElementCount,
    int ChangedAttributeCount);
