namespace Veyra.Application.DTOs.PendingChanges;

public sealed record PendingAudioDiffPreviewDto
{
    public string BaselineAudioPath { get; init; } = string.Empty;
    public bool IsBaselineTempFile { get; init; }

    public string CurrentAudioPath { get; init; } = string.Empty;
    public bool IsCurrentTempFile { get; init; }

    public string DifferenceAudioPath { get; init; } = string.Empty;
    public bool IsDifferenceTempFile { get; init; }

    public string WaveformImagePath { get; init; } = string.Empty;
    public bool IsWaveformTempFile { get; init; }

    public string SpectrogramImagePath { get; init; } = string.Empty;
    public bool IsSpectrogramTempFile { get; init; }

    public string SpectralDeltaImagePath { get; init; } = string.Empty;
    public bool IsSpectralDeltaTempFile { get; init; }

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
    public double? SpectralSimilarityRatio { get; init; }
    public double? SpectralDeltaRatio { get; init; }
    public double? BaselineStereoCorrelation { get; init; }
    public double? CurrentStereoCorrelation { get; init; }
    public double? ChangedTimeRatio { get; init; }
    public int ChangedSegmentCount { get; init; }

    public bool HasDurationMismatch { get; init; }

    public IReadOnlyList<PendingAudioChannelMetricDto> ChannelMetrics { get; init; } = Array.Empty<PendingAudioChannelMetricDto>();
    public IReadOnlyList<PendingAudioBandMetricDto> BandMetrics { get; init; } = Array.Empty<PendingAudioBandMetricDto>();
    public IReadOnlyList<PendingAudioChangedSegmentDto> ChangedSegments { get; init; } = Array.Empty<PendingAudioChangedSegmentDto>();
}
