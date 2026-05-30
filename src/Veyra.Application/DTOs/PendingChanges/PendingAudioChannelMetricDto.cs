namespace Veyra.Application.DTOs.PendingChanges;

public sealed record PendingAudioChannelMetricDto
{
    public int ChannelIndex { get; init; }
    public string ChannelDisplayName { get; init; } = string.Empty;

    public double? BaselinePeakAmplitude { get; init; }
    public double? CurrentPeakAmplitude { get; init; }

    public double? BaselineRmsAmplitude { get; init; }
    public double? CurrentRmsAmplitude { get; init; }

    public double? SimilarityRatio { get; init; }
    public double? ChangedTimeRatio { get; init; }
}
