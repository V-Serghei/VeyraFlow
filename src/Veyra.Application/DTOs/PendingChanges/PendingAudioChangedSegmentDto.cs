namespace Veyra.Application.DTOs.PendingChanges;

public sealed record PendingAudioChangedSegmentDto
{
    public int SegmentIndex { get; init; }
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public double AverageDifferenceRatio { get; init; }
    public double PeakDifferenceRatio { get; init; }
}
