namespace Veyra.Application.DTOs;

public sealed record PendingAudioDiffPreviewDto
{
    public string BaselineAudioPath { get; init; } = string.Empty;
    public bool IsBaselineTempFile { get; init; }

    public string CurrentAudioPath { get; init; } = string.Empty;
    public bool IsCurrentTempFile { get; init; }

    public string WaveformImagePath { get; init; } = string.Empty;
    public bool IsWaveformTempFile { get; init; }

    public double? BaselineDurationSeconds { get; init; }
    public double? CurrentDurationSeconds { get; init; }

    public int? BaselineSampleRate { get; init; }
    public int? CurrentSampleRate { get; init; }

    public int? BaselineChannels { get; init; }
    public int? CurrentChannels { get; init; }

    public double? BaselinePeakAmplitude { get; init; }
    public double? CurrentPeakAmplitude { get; init; }

    public double? BaselineRmsAmplitude { get; init; }
    public double? CurrentRmsAmplitude { get; init; }

    public double? SignalSimilarityRatio { get; init; }
    public double? ChangedTimeRatio { get; init; }
    public int ChangedSegmentCount { get; init; }

    public bool HasDurationMismatch { get; init; }
}
