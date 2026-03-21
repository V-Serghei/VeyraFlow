namespace Veyra.Infrastructure.Data.Preview;

public sealed record AudioChangedSegment(
    int SegmentIndex,
    double StartSeconds,
    double EndSeconds,
    double DurationSeconds,
    double AverageDifferenceRatio,
    double PeakDifferenceRatio);
