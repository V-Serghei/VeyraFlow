using NAudio.Dsp;
using NAudio.Wave;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Veyra.Infrastructure.Data.Preview;

public sealed record AudioDiffPreviewBuildResult(
    string DifferenceAudioPath,
    bool IsDifferenceTempFile,
    string WaveformImagePath,
    bool IsWaveformTempFile,
    string SpectrogramImagePath,
    bool IsSpectrogramTempFile,
    string SpectralDeltaImagePath,
    bool IsSpectralDeltaTempFile,
    double BaselineDurationSeconds,
    double CurrentDurationSeconds,
    int BaselineSampleRate,
    int CurrentSampleRate,
    int BaselineChannels,
    int CurrentChannels,
    double BaselinePeakAmplitude,
    double CurrentPeakAmplitude,
    double BaselineRmsAmplitude,
    double CurrentRmsAmplitude,
    double SignalSimilarityRatio,
    double SpectralSimilarityRatio,
    double SpectralDeltaRatio,
    double ChangedTimeRatio,
    int ChangedSegmentCount,
    bool HasDurationMismatch,
    double? BaselineStereoCorrelation,
    double? CurrentStereoCorrelation,
    IReadOnlyList<AudioChannelDiffMetric> ChannelMetrics,
    IReadOnlyList<AudioBandDiffMetric> BandMetrics,
    IReadOnlyList<AudioChangedSegment> ChangedSegments);

public sealed record AudioChannelDiffMetric(
    int ChannelIndex,
    string ChannelDisplayName,
    double BaselinePeakAmplitude,
    double CurrentPeakAmplitude,
    double BaselineRmsAmplitude,
    double CurrentRmsAmplitude,
    double SimilarityRatio,
    double ChangedTimeRatio);

public sealed record AudioChangedSegment(
    int SegmentIndex,
    double StartSeconds,
    double EndSeconds,
    double DurationSeconds,
    double AverageDifferenceRatio,
    double PeakDifferenceRatio);

public sealed record AudioBandDiffMetric(
    string BandDisplayName,
    double BaselineEnergyRatio,
    double CurrentEnergyRatio,
    double DeltaRatio,
    double SimilarityRatio);

public static class AudioDiffPreviewBuilder
{
    private const int BucketCount = 1400;
    private const int WaveformImageWidth = 1400;
    private const int WaveformTrackHeight = 84;
    private const int WaveformMargin = 18;
    private const int WaveformTrackGap = 14;
    private const int DiffBandHeight = 44;

    private const int SpectrogramColumns = 720;
    private const int SpectrogramBins = 196;
    private const int SpectrogramPanelHeight = 170;
    private const int SpectrogramImageWidth = 1280;
    private const int SpectrogramImageMargin = 18;
    private const int SpectrogramGap = 14;
    private const int SpectralDeltaPanelHeight = 220;
    private const int FftSize = 1024;

    public static async Task<AudioDiffPreviewBuildResult?> TryBuildAsync(
        string baselinePath,
        string currentPath,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baselinePath)
            || string.IsNullOrWhiteSpace(currentPath)
            || !File.Exists(baselinePath)
            || !File.Exists(currentPath))
        {
            return null;
        }

        string? waveformPath = null;
        string? spectrogramPath = null;
        string? spectralDeltaPath = null;
        string? differenceAudioPath = null;

        try
        {
            var baseline = await Task.Run(() => AnalyzeSignal(baselinePath, BucketCount, ct), ct);
            var current = await Task.Run(() => AnalyzeSignal(currentPath, BucketCount, ct), ct);
            if (baseline is null || current is null)
                return null;

            var displayChannels = Math.Max(baseline.DisplayChannelCount, current.DisplayChannelCount);
            displayChannels = Math.Max(displayChannels, 1);

            var channelDiffs = BuildChannelDiffs(baseline, current, displayChannels);
            var combinedDiff = BuildCombinedDiffStrength(channelDiffs.Select(metric => metric.DiffStrength).ToArray());
            var changedBuckets = BuildChangedBucketMask(combinedDiff);
            var maxDuration = Math.Max(baseline.DurationSeconds, current.DurationSeconds);
            var changedSegments = BuildChangedSegments(combinedDiff, changedBuckets, maxDuration);
            var similarity = Math.Clamp(1d - combinedDiff.Average(), 0d, 1d);
            var changedRatio = changedBuckets.Length == 0
                ? 0d
                : Math.Clamp(changedBuckets.Count(x => x) / (double)changedBuckets.Length, 0d, 1d);

            var baselineSpectrogram = BuildSpectrogramMatrix(baseline.MonoSamples, ct);
            var currentSpectrogram = BuildSpectrogramMatrix(current.MonoSamples, ct);
            var spectralDelta = BuildSpectralDeltaMatrix(baselineSpectrogram, currentSpectrogram);
            var spectralDeltaRatio = spectralDelta.Length == 0
                ? 0d
                : spectralDelta.SelectMany(row => row).DefaultIfEmpty(0d).Average();
            var spectralSimilarity = Math.Clamp(1d - spectralDeltaRatio, 0d, 1d);
            var bandMetrics = BuildBandMetrics(
                baselineSpectrogram,
                currentSpectrogram,
                Math.Max(baseline.SampleRate, current.SampleRate));

            var tempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "audio-diff-preview");
            Directory.CreateDirectory(tempDir);

            differenceAudioPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.audio-difference.wav");
            waveformPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.audio-waveform.png");
            spectrogramPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.audio-spectrogram.png");
            spectralDeltaPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.audio-spectral-delta.png");

            await RenderDifferenceAudioAsync(differenceAudioPath, baseline, current, ct);
            await RenderWaveformAsync(waveformPath, baseline, current, combinedDiff, changedBuckets, displayChannels, ct);
            await RenderSpectrogramComparisonAsync(spectrogramPath, baselineSpectrogram, currentSpectrogram, ct);
            await RenderSpectralDeltaAsync(spectralDeltaPath, spectralDelta, ct);

            return new AudioDiffPreviewBuildResult(
                DifferenceAudioPath: differenceAudioPath,
                IsDifferenceTempFile: true,
                WaveformImagePath: waveformPath,
                IsWaveformTempFile: true,
                SpectrogramImagePath: spectrogramPath,
                IsSpectrogramTempFile: true,
                SpectralDeltaImagePath: spectralDeltaPath,
                IsSpectralDeltaTempFile: true,
                BaselineDurationSeconds: baseline.DurationSeconds,
                CurrentDurationSeconds: current.DurationSeconds,
                BaselineSampleRate: baseline.SampleRate,
                CurrentSampleRate: current.SampleRate,
                BaselineChannels: baseline.OriginalChannelCount,
                CurrentChannels: current.OriginalChannelCount,
                BaselinePeakAmplitude: baseline.PeakAmplitude,
                CurrentPeakAmplitude: current.PeakAmplitude,
                BaselineRmsAmplitude: baseline.RmsAmplitude,
                CurrentRmsAmplitude: current.RmsAmplitude,
                SignalSimilarityRatio: similarity,
                SpectralSimilarityRatio: spectralSimilarity,
                SpectralDeltaRatio: spectralDeltaRatio,
                ChangedTimeRatio: changedRatio,
                ChangedSegmentCount: changedSegments.Count,
                HasDurationMismatch: Math.Abs(baseline.DurationSeconds - current.DurationSeconds) >= 0.05d,
                BaselineStereoCorrelation: baseline.StereoCorrelation,
                CurrentStereoCorrelation: current.StereoCorrelation,
                ChannelMetrics: channelDiffs
                    .Select(metric => new AudioChannelDiffMetric(
                        metric.ChannelIndex,
                        metric.ChannelDisplayName,
                        metric.BaselinePeakAmplitude,
                        metric.CurrentPeakAmplitude,
                        metric.BaselineRmsAmplitude,
                        metric.CurrentRmsAmplitude,
                        metric.SimilarityRatio,
                        metric.ChangedTimeRatio))
                    .ToArray(),
                BandMetrics: bandMetrics,
                ChangedSegments: changedSegments);
        }
        catch (OperationCanceledException)
        {
            TryDelete(differenceAudioPath);
            TryDelete(waveformPath);
            TryDelete(spectrogramPath);
            TryDelete(spectralDeltaPath);
            throw;
        }
        catch
        {
            TryDelete(differenceAudioPath);
            TryDelete(waveformPath);
            TryDelete(spectrogramPath);
            TryDelete(spectralDeltaPath);
            return null;
        }
    }

    private static AudioSignalAnalysis? AnalyzeSignal(string path, int bucketCount, CancellationToken ct)
    {
        using var reader = new AudioFileReader(path);
        var sampleRate = Math.Max(1, reader.WaveFormat.SampleRate);
        var originalChannels = Math.Max(1, reader.WaveFormat.Channels);
        var displayChannelCount = Math.Min(originalChannels, 2);
        var durationSeconds = Math.Max(0d, reader.TotalTime.TotalSeconds);
        var totalFrames = Math.Max(1L, reader.Length / Math.Max(1, reader.WaveFormat.BlockAlign));

        var peaks = new float[displayChannelCount][];
        var rmsAccumulator = new double[displayChannelCount][];
        var bucketSampleCounts = new int[displayChannelCount][];
        var channelPeaks = new double[displayChannelCount];
        var channelSquares = new double[displayChannelCount];
        var channelTotalSamples = new long[displayChannelCount];

        for (var i = 0; i < displayChannelCount; i++)
        {
            peaks[i] = new float[bucketCount];
            rmsAccumulator[i] = new double[bucketCount];
            bucketSampleCounts[i] = new int[bucketCount];
        }

        var monoSamples = new List<float>();
        var monoPeak = 0d;
        var monoSquare = 0d;
        var monoTotalSamples = 0L;

        double stereoCross = 0d;
        double stereoLeftSquare = 0d;
        double stereoRightSquare = 0d;

        var frameIndex = 0L;
        var buffer = new float[Math.Max(8192, sampleRate / 2) * originalChannels];
        int read;

        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();

            var framesRead = read / originalChannels;
            for (var frame = 0; frame < framesRead; frame++)
            {
                var baseOffset = frame * originalChannels;
                double monoSample = 0d;

                for (var ch = 0; ch < originalChannels; ch++)
                    monoSample += buffer[baseOffset + ch];

                monoSample /= originalChannels;
                monoSamples.Add((float)monoSample);

                var monoAbs = Math.Abs(monoSample);
                monoPeak = Math.Max(monoPeak, monoAbs);
                monoSquare += monoSample * monoSample;
                monoTotalSamples++;

                var bucketIndex = totalFrames <= 1
                    ? 0
                    : (int)Math.Min(bucketCount - 1, (frameIndex * bucketCount) / totalFrames);

                for (var ch = 0; ch < displayChannelCount; ch++)
                {
                    var channelSample = buffer[baseOffset + ch];
                    var abs = Math.Abs(channelSample);

                    channelPeaks[ch] = Math.Max(channelPeaks[ch], abs);
                    channelSquares[ch] += channelSample * channelSample;
                    channelTotalSamples[ch]++;

                    peaks[ch][bucketIndex] = Math.Max(peaks[ch][bucketIndex], (float)abs);
                    rmsAccumulator[ch][bucketIndex] += channelSample * channelSample;
                    bucketSampleCounts[ch][bucketIndex]++;
                }

                if (displayChannelCount >= 2)
                {
                    var left = buffer[baseOffset];
                    var right = buffer[baseOffset + 1];
                    stereoCross += left * right;
                    stereoLeftSquare += left * left;
                    stereoRightSquare += right * right;
                }

                frameIndex++;
            }
        }

        var channels = new AudioChannelAnalysis[displayChannelCount];
        for (var i = 0; i < displayChannelCount; i++)
        {
            var rms = new float[bucketCount];
            for (var bucket = 0; bucket < bucketCount; bucket++)
            {
                rms[bucket] = bucketSampleCounts[i][bucket] <= 0
                    ? 0f
                    : (float)Math.Sqrt(rmsAccumulator[i][bucket] / bucketSampleCounts[i][bucket]);
            }

            var totalRms = channelTotalSamples[i] <= 0
                ? 0d
                : Math.Sqrt(channelSquares[i] / channelTotalSamples[i]);

            channels[i] = new AudioChannelAnalysis(
                ChannelIndex: i,
                ChannelDisplayName: GetChannelDisplayName(i, displayChannelCount),
                Peaks: peaks[i],
                Rms: rms,
                PeakAmplitude: channelPeaks[i],
                RmsAmplitude: totalRms);
        }

        var monoRms = monoTotalSamples <= 0
            ? 0d
            : Math.Sqrt(monoSquare / monoTotalSamples);

        double? stereoCorrelation = null;
        if (displayChannelCount >= 2 && stereoLeftSquare > 0d && stereoRightSquare > 0d)
            stereoCorrelation = Math.Clamp(stereoCross / Math.Sqrt(stereoLeftSquare * stereoRightSquare), -1d, 1d);

        return new AudioSignalAnalysis(
            Channels: channels,
            MonoSamples: monoSamples.Count == 0 ? [0f] : monoSamples.ToArray(),
            DurationSeconds: durationSeconds,
            SampleRate: sampleRate,
            OriginalChannelCount: originalChannels,
            DisplayChannelCount: displayChannelCount,
            PeakAmplitude: monoPeak,
            RmsAmplitude: monoRms,
            StereoCorrelation: stereoCorrelation);
    }

    private static IReadOnlyList<AudioChannelDiffSeries> BuildChannelDiffs(
        AudioSignalAnalysis baseline,
        AudioSignalAnalysis current,
        int displayChannels)
    {
        var result = new List<AudioChannelDiffSeries>(displayChannels);

        for (var channelIndex = 0; channelIndex < displayChannels; channelIndex++)
        {
            var baselineChannel = GetChannelOrSilent(baseline, channelIndex, displayChannels);
            var currentChannel = GetChannelOrSilent(current, channelIndex, displayChannels);
            var diffStrength = BuildDiffStrength(baselineChannel.Peaks, baselineChannel.Rms, currentChannel.Peaks, currentChannel.Rms);
            var changedMask = BuildChangedBucketMask(diffStrength);
            var similarity = Math.Clamp(1d - diffStrength.Average(), 0d, 1d);
            var changedRatio = changedMask.Length == 0
                ? 0d
                : Math.Clamp(changedMask.Count(x => x) / (double)changedMask.Length, 0d, 1d);

            result.Add(new AudioChannelDiffSeries(
                ChannelIndex: channelIndex,
                ChannelDisplayName: baselineChannel.ChannelDisplayName,
                BaselinePeakAmplitude: baselineChannel.PeakAmplitude,
                CurrentPeakAmplitude: currentChannel.PeakAmplitude,
                BaselineRmsAmplitude: baselineChannel.RmsAmplitude,
                CurrentRmsAmplitude: currentChannel.RmsAmplitude,
                SimilarityRatio: similarity,
                ChangedTimeRatio: changedRatio,
                DiffStrength: diffStrength));
        }

        return result;
    }

    private static AudioChannelAnalysis GetChannelOrSilent(AudioSignalAnalysis analysis, int channelIndex, int displayChannels)
    {
        if (channelIndex < analysis.Channels.Length)
            return analysis.Channels[channelIndex];

        var silentPeaks = new float[BucketCount];
        var silentRms = new float[BucketCount];
        return new AudioChannelAnalysis(
            ChannelIndex: channelIndex,
            ChannelDisplayName: GetChannelDisplayName(channelIndex, displayChannels),
            Peaks: silentPeaks,
            Rms: silentRms,
            PeakAmplitude: 0d,
            RmsAmplitude: 0d);
    }

    private static string GetChannelDisplayName(int channelIndex, int displayChannels)
    {
        if (displayChannels <= 1)
            return "Mono";

        return channelIndex switch
        {
            0 => "L",
            1 => "R",
            _ => $"Ch {channelIndex + 1}"
        };
    }

    private static double[] BuildCombinedDiffStrength(IReadOnlyList<double[]> channelDiffs)
    {
        if (channelDiffs.Count == 0)
            return Array.Empty<double>();

        if (channelDiffs.Count == 1)
            return channelDiffs[0];

        var length = channelDiffs.Min(series => series.Length);
        var combined = new double[length];

        for (var i = 0; i < length; i++)
        {
            var max = 0d;
            var sum = 0d;

            foreach (var series in channelDiffs)
            {
                max = Math.Max(max, series[i]);
                sum += series[i];
            }

            var average = sum / channelDiffs.Count;
            combined[i] = Math.Clamp((max * 0.7d) + (average * 0.3d), 0d, 1d);
        }

        return Smooth(combined);
    }

    private static double[] BuildDiffStrength(float[] baselinePeaks, float[] baselineRms, float[] currentPeaks, float[] currentRms)
    {
        var bucketCount = new[] { baselinePeaks.Length, baselineRms.Length, currentPeaks.Length, currentRms.Length }.Min();
        var diff = new double[bucketCount];
        for (var i = 0; i < bucketCount; i++)
        {
            var peakDelta = Math.Abs(baselinePeaks[i] - currentPeaks[i]);
            var rmsDelta = Math.Abs(baselineRms[i] - currentRms[i]);
            diff[i] = Math.Clamp((peakDelta * 0.58d) + (rmsDelta * 0.42d), 0d, 1d);
        }

        return Smooth(diff);
    }

    private static double[] Smooth(double[] values)
    {
        if (values.Length <= 2)
            return values;

        var smoothed = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var prev = i > 0 ? values[i - 1] : values[i];
            var next = i + 1 < values.Length ? values[i + 1] : values[i];
            smoothed[i] = Math.Clamp((prev + values[i] + next) / 3d, 0d, 1d);
        }

        return smoothed;
    }

    private static bool[] BuildChangedBucketMask(double[] diffStrength)
    {
        if (diffStrength.Length == 0)
            return [];

        var average = diffStrength.Average();
        var threshold = Math.Clamp(Math.Max(0.014d, average * 1.24d), 0.014d, 0.11d);
        var changed = diffStrength.Select(x => x >= threshold).ToArray();

        for (var i = 1; i < changed.Length - 1; i++)
        {
            if (!changed[i] && changed[i - 1] && changed[i + 1])
                changed[i] = true;
        }

        for (var i = 1; i < changed.Length - 1; i++)
        {
            if (changed[i] && !changed[i - 1] && !changed[i + 1] && diffStrength[i] < threshold * 1.5d)
                changed[i] = false;
        }

        return changed;
    }

    private static IReadOnlyList<AudioChangedSegment> BuildChangedSegments(
        double[] diffStrength,
        bool[] changedBuckets,
        double durationSeconds)
    {
        if (changedBuckets.Length == 0)
            return Array.Empty<AudioChangedSegment>();

        var segments = new List<AudioChangedSegment>();
        var start = -1;

        for (var i = 0; i <= changedBuckets.Length; i++)
        {
            var isChanged = i < changedBuckets.Length && changedBuckets[i];
            if (isChanged && start < 0)
            {
                start = i;
                continue;
            }

            if (isChanged || start < 0)
                continue;

            var end = i - 1;
            var startSeconds = durationSeconds * start / changedBuckets.Length;
            var endSeconds = durationSeconds * (end + 1) / changedBuckets.Length;
            var segmentDiff = diffStrength.Skip(start).Take(end - start + 1).ToArray();

            segments.Add(new AudioChangedSegment(
                SegmentIndex: segments.Count + 1,
                StartSeconds: startSeconds,
                EndSeconds: endSeconds,
                DurationSeconds: Math.Max(0d, endSeconds - startSeconds),
                AverageDifferenceRatio: segmentDiff.Length == 0 ? 0d : segmentDiff.Average(),
                PeakDifferenceRatio: segmentDiff.Length == 0 ? 0d : segmentDiff.Max()));

            start = -1;
        }

        return segments;
    }

    private static double[][] BuildSpectrogramMatrix(float[] samples, CancellationToken ct)
    {
        var normalizedSamples = samples.Length == 0 ? [0f] : samples;
        var usableStartRange = Math.Max(0, normalizedSamples.Length - FftSize);
        var fftExponent = (int)Math.Log2(FftSize);
        var window = BuildHannWindow(FftSize);
        var matrix = new double[SpectrogramBins][];
        for (var row = 0; row < SpectrogramBins; row++)
            matrix[row] = new double[SpectrogramColumns];

        var rawBins = FftSize / 2;
        var maxMagnitude = 0d;

        for (var column = 0; column < SpectrogramColumns; column++)
        {
            ct.ThrowIfCancellationRequested();

            var start = SpectrogramColumns <= 1
                ? 0
                : (int)Math.Round(usableStartRange * (column / (double)(SpectrogramColumns - 1)));
            var fftBuffer = new Complex[FftSize];

            for (var sampleIndex = 0; sampleIndex < FftSize; sampleIndex++)
            {
                var sourceIndex = start + sampleIndex;
                var sample = sourceIndex < normalizedSamples.Length
                    ? normalizedSamples[sourceIndex]
                    : 0f;
                fftBuffer[sampleIndex].X = (float)(sample * window[sampleIndex]);
                fftBuffer[sampleIndex].Y = 0f;
            }

            FastFourierTransform.FFT(true, fftExponent, fftBuffer);

            var magnitudes = new double[rawBins];
            for (var bin = 0; bin < rawBins; bin++)
            {
                var x = fftBuffer[bin].X;
                var y = fftBuffer[bin].Y;
                var magnitude = Math.Sqrt((x * x) + (y * y));
                magnitudes[bin] = magnitude;
                maxMagnitude = Math.Max(maxMagnitude, magnitude);
            }

            for (var row = 0; row < SpectrogramBins; row++)
            {
                var rawIndex = MapDisplayRowToRawBin(row, SpectrogramBins, rawBins);
                matrix[row][column] = magnitudes[rawIndex];
            }
        }

        if (maxMagnitude <= double.Epsilon)
            return matrix;

        for (var row = 0; row < SpectrogramBins; row++)
        {
            for (var column = 0; column < SpectrogramColumns; column++)
            {
                var normalized = Math.Clamp(matrix[row][column] / maxMagnitude, 0d, 1d);
                matrix[row][column] = Math.Clamp(Math.Log10(1d + (normalized * 9d)), 0d, 1d);
            }
        }

        return matrix;
    }

    private static int MapDisplayRowToRawBin(int row, int displayBins, int rawBins)
    {
        if (rawBins <= 1)
            return 0;

        var normalized = displayBins <= 1
            ? 1d
            : row / (double)(displayBins - 1);
        var scaled = (Math.Pow(rawBins, normalized) - 1d) / (rawBins - 1d);
        var rawIndex = (int)Math.Round(scaled * (rawBins - 1));
        return Math.Clamp(rawIndex, 0, rawBins - 1);
    }

    private static double[][] BuildSpectralDeltaMatrix(double[][] baselineSpectrogram, double[][] currentSpectrogram)
    {
        var rows = Math.Min(baselineSpectrogram.Length, currentSpectrogram.Length);
        if (rows == 0)
            return Array.Empty<double[]>();

        var columns = Math.Min(baselineSpectrogram[0].Length, currentSpectrogram[0].Length);
        var delta = new double[rows][];
        for (var row = 0; row < rows; row++)
        {
            delta[row] = new double[columns];
            for (var column = 0; column < columns; column++)
                delta[row][column] = Math.Abs(baselineSpectrogram[row][column] - currentSpectrogram[row][column]);
        }

        return delta;
    }

    private static IReadOnlyList<AudioBandDiffMetric> BuildBandMetrics(
        double[][] baselineSpectrogram,
        double[][] currentSpectrogram,
        int sampleRate)
    {
        if (baselineSpectrogram.Length == 0 || currentSpectrogram.Length == 0)
            return Array.Empty<AudioBandDiffMetric>();

        var nyquist = Math.Max(1d, sampleRate / 2d);
        var definitions = new (string Name, double MinHz, double MaxHz)[]
        {
            ("Low", 0d, 250d),
            ("Mids", 250d, 4000d),
            ("Highs", 4000d, nyquist)
        };

        var result = new List<AudioBandDiffMetric>(definitions.Length);
        foreach (var definition in definitions)
        {
            var (baselineAverage, currentAverage) = GetBandEnergyAverages(
                baselineSpectrogram,
                currentSpectrogram,
                sampleRate,
                definition.MinHz,
                definition.MaxHz);

            var delta = Math.Abs(baselineAverage - currentAverage);
            result.Add(new AudioBandDiffMetric(
                definition.Name,
                baselineAverage,
                currentAverage,
                delta,
                Math.Clamp(1d - delta, 0d, 1d)));
        }

        return result;
    }

    private static (double BaselineAverage, double CurrentAverage) GetBandEnergyAverages(
        double[][] baselineSpectrogram,
        double[][] currentSpectrogram,
        int sampleRate,
        double minHz,
        double maxHz)
    {
        var rows = Math.Min(baselineSpectrogram.Length, currentSpectrogram.Length);
        if (rows == 0)
            return (0d, 0d);

        var baselineValues = new List<double>();
        var currentValues = new List<double>();

        for (var row = 0; row < rows; row++)
        {
            var frequency = EstimateDisplayRowFrequency(row, rows, sampleRate);
            var inBand = frequency >= minHz && (row == rows - 1 || frequency < maxHz || maxHz >= sampleRate / 2d);
            if (!inBand)
                continue;

            baselineValues.AddRange(baselineSpectrogram[row]);
            currentValues.AddRange(currentSpectrogram[row]);
        }

        return (
            baselineValues.Count == 0 ? 0d : baselineValues.Average(),
            currentValues.Count == 0 ? 0d : currentValues.Average());
    }

    private static double EstimateDisplayRowFrequency(int row, int totalRows, int sampleRate)
    {
        var rawBins = FftSize / 2;
        var rawIndex = MapDisplayRowToRawBin(row, totalRows, rawBins);
        return rawIndex * sampleRate / (double)FftSize;
    }

    private static async Task RenderDifferenceAudioAsync(
        string outputPath,
        AudioSignalAnalysis baseline,
        AudioSignalAnalysis current,
        CancellationToken ct)
    {
        var targetSampleRate = Math.Max(baseline.SampleRate, current.SampleRate);
        var targetDuration = Math.Max(baseline.DurationSeconds, current.DurationSeconds);
        var sampleCount = Math.Max(1, (int)Math.Ceiling(targetDuration * targetSampleRate));
        var diffSamples = new float[sampleCount];

        for (var i = 0; i < sampleCount; i++)
        {
            if ((i & 2047) == 0)
                ct.ThrowIfCancellationRequested();

            var time = i / (double)targetSampleRate;
            var baselineSample = SampleAtTime(baseline.MonoSamples, baseline.SampleRate, time);
            var currentSample = SampleAtTime(current.MonoSamples, current.SampleRate, time);
            diffSamples[i] = (float)Math.Clamp((currentSample - baselineSample) * 0.85d, -1d, 1d);
        }

        await WriteMonoWaveAsync(outputPath, targetSampleRate, diffSamples, ct);
    }

    private static double SampleAtTime(float[] samples, int sampleRate, double timeSeconds)
    {
        if (samples.Length == 0 || sampleRate <= 0 || timeSeconds < 0d)
            return 0d;

        var position = timeSeconds * sampleRate;
        var leftIndex = (int)Math.Floor(position);
        if (leftIndex < 0 || leftIndex >= samples.Length)
            return 0d;

        var rightIndex = Math.Min(samples.Length - 1, leftIndex + 1);
        var fraction = Math.Clamp(position - leftIndex, 0d, 1d);
        return samples[leftIndex] + ((samples[rightIndex] - samples[leftIndex]) * fraction);
    }

    private static async Task WriteMonoWaveAsync(string path, int sampleRate, float[] samples, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        const short bitsPerSample = 16;
        const short channelCount = 1;
        var blockAlign = (short)(channelCount * (bitsPerSample / 8));
        var byteRate = sampleRate * blockAlign;
        var dataLength = samples.Length * blockAlign;

        await using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channelCount);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        foreach (var sample in samples)
        {
            ct.ThrowIfCancellationRequested();
            var pcm = (short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue);
            writer.Write(pcm);
        }

        writer.Flush();
        await stream.FlushAsync(ct);
    }

    private static async Task RenderWaveformAsync(
        string outputPath,
        AudioSignalAnalysis baseline,
        AudioSignalAnalysis current,
        double[] combinedDiff,
        bool[] changedBuckets,
        int displayChannels,
        CancellationToken ct)
    {
        var topRows = new List<int>(displayChannels);
        var bottomRows = new List<int>(displayChannels);
        var y = WaveformMargin;

        for (var channel = 0; channel < displayChannels; channel++)
        {
            topRows.Add(y);
            y += WaveformTrackHeight + WaveformTrackGap;
        }

        var diffTop = y;
        y += DiffBandHeight + WaveformTrackGap;

        for (var channel = 0; channel < displayChannels; channel++)
        {
            bottomRows.Add(y);
            y += WaveformTrackHeight + WaveformTrackGap;
        }

        var imageHeight = y - WaveformTrackGap + WaveformMargin;
        using var image = new Image<Rgba32>(WaveformImageWidth, imageHeight, new Rgba32(16, 20, 28, 255));

        for (var channel = 0; channel < displayChannels; channel++)
        {
            DrawPanelBackground(image, topRows[channel], WaveformTrackHeight);
            DrawPanelBackground(image, bottomRows[channel], WaveformTrackHeight);
            DrawCenterLine(image, topRows[channel] + (WaveformTrackHeight / 2));
            DrawCenterLine(image, bottomRows[channel] + (WaveformTrackHeight / 2));
            DrawBorder(image, topRows[channel], WaveformTrackHeight, new Rgba32(54, 66, 89, 255));
            DrawBorder(image, bottomRows[channel], WaveformTrackHeight, new Rgba32(54, 66, 89, 255));
        }

        DrawPanelBackground(image, diffTop, DiffBandHeight);
        DrawBorder(image, diffTop, DiffBandHeight, new Rgba32(54, 66, 89, 255));

        var halfHeight = Math.Max(1, (WaveformTrackHeight / 2) - 5);

        for (var x = 0; x < WaveformImageWidth; x++)
        {
            ct.ThrowIfCancellationRequested();

            var bucketIndex = Math.Min(BucketCount - 1, (int)((x / (double)Math.Max(1, WaveformImageWidth - 1)) * (BucketCount - 1)));
            var isChanged = changedBuckets.Length > bucketIndex && changedBuckets[bucketIndex];
            var diff = combinedDiff.Length > bucketIndex ? combinedDiff[bucketIndex] : 0d;

            for (var channel = 0; channel < displayChannels; channel++)
            {
                var baselineChannel = GetChannelOrSilent(baseline, channel, displayChannels);
                var currentChannel = GetChannelOrSilent(current, channel, displayChannels);
                var baselinePalette = GetWaveformPalette(channel, true, isChanged);
                var currentPalette = GetWaveformPalette(channel, false, isChanged);

                DrawWaveformColumn(
                    image,
                    x,
                    topRows[channel] + (WaveformTrackHeight / 2),
                    halfHeight,
                    baselineChannel.Peaks[bucketIndex],
                    baselineChannel.Rms[bucketIndex],
                    baselinePalette.Outer,
                    baselinePalette.Inner);

                DrawWaveformColumn(
                    image,
                    x,
                    bottomRows[channel] + (WaveformTrackHeight / 2),
                    halfHeight,
                    currentChannel.Peaks[bucketIndex],
                    currentChannel.Rms[bucketIndex],
                    currentPalette.Outer,
                    currentPalette.Inner);
            }

            DrawDiffColumn(image, x, diffTop, DiffBandHeight, diff, isChanged);
        }

        await using var stream = File.Create(outputPath);
        await image.SaveAsPngAsync(stream, new PngEncoder(), ct);
    }

    private static (Rgba32 Outer, Rgba32 Inner) GetWaveformPalette(int channelIndex, bool isBaseline, bool emphasized)
    {
        if (isBaseline)
        {
            return channelIndex switch
            {
                0 => emphasized
                    ? (new Rgba32(110, 182, 255, 255), new Rgba32(180, 220, 255, 255))
                    : (new Rgba32(84, 132, 214, 255), new Rgba32(128, 176, 236, 255)),
                1 => emphasized
                    ? (new Rgba32(190, 132, 255, 255), new Rgba32(224, 186, 255, 255))
                    : (new Rgba32(138, 104, 220, 255), new Rgba32(183, 148, 238, 255)),
                _ => emphasized
                    ? (new Rgba32(144, 190, 255, 255), new Rgba32(207, 229, 255, 255))
                    : (new Rgba32(108, 146, 214, 255), new Rgba32(159, 192, 236, 255))
            };
        }

        return channelIndex switch
        {
            0 => emphasized
                ? (new Rgba32(99, 232, 173, 255), new Rgba32(188, 250, 218, 255))
                : (new Rgba32(71, 188, 142, 255), new Rgba32(125, 219, 179, 255)),
            1 => emphasized
                ? (new Rgba32(255, 177, 102, 255), new Rgba32(255, 219, 178, 255))
                : (new Rgba32(219, 145, 80, 255), new Rgba32(242, 183, 130, 255)),
            _ => emphasized
                ? (new Rgba32(132, 227, 198, 255), new Rgba32(198, 245, 230, 255))
                : (new Rgba32(89, 190, 166, 255), new Rgba32(145, 220, 200, 255))
        };
    }

    private static async Task RenderSpectrogramComparisonAsync(
        string outputPath,
        double[][] baselineSpectrogram,
        double[][] currentSpectrogram,
        CancellationToken ct)
    {
        var imageHeight = SpectrogramImageMargin * 2 + SpectrogramPanelHeight * 2 + SpectrogramGap;
        using var image = new Image<Rgba32>(SpectrogramImageWidth, imageHeight, new Rgba32(16, 20, 28, 255));

        var top = SpectrogramImageMargin;
        var bottom = top + SpectrogramPanelHeight + SpectrogramGap;

        DrawPanelBackground(image, top, SpectrogramPanelHeight);
        DrawPanelBackground(image, bottom, SpectrogramPanelHeight);

        DrawSpectrogramPanel(image, baselineSpectrogram, top, SpectrogramPanelHeight, SpectrogramPalette);
        DrawSpectrogramPanel(image, currentSpectrogram, bottom, SpectrogramPanelHeight, SpectrogramPalette);

        DrawBorder(image, top, SpectrogramPanelHeight, new Rgba32(54, 66, 89, 255));
        DrawBorder(image, bottom, SpectrogramPanelHeight, new Rgba32(54, 66, 89, 255));

        await using var stream = File.Create(outputPath);
        await image.SaveAsPngAsync(stream, new PngEncoder(), ct);
    }

    private static async Task RenderSpectralDeltaAsync(
        string outputPath,
        double[][] spectralDelta,
        CancellationToken ct)
    {
        var imageHeight = SpectrogramImageMargin * 2 + SpectralDeltaPanelHeight;
        using var image = new Image<Rgba32>(SpectrogramImageWidth, imageHeight, new Rgba32(16, 20, 28, 255));

        var top = SpectrogramImageMargin;
        DrawPanelBackground(image, top, SpectralDeltaPanelHeight);
        DrawSpectrogramPanel(image, spectralDelta, top, SpectralDeltaPanelHeight, SpectralDeltaPalette);
        DrawBorder(image, top, SpectralDeltaPanelHeight, new Rgba32(54, 66, 89, 255));

        await using var stream = File.Create(outputPath);
        await image.SaveAsPngAsync(stream, new PngEncoder(), ct);
    }

    private static void DrawSpectrogramPanel(
        Image<Rgba32> image,
        double[][] matrix,
        int top,
        int height,
        Func<double, Rgba32> palette)
    {
        if (matrix.Length == 0 || matrix[0].Length == 0)
            return;

        var rows = matrix.Length;
        var columns = matrix[0].Length;

        for (var x = 0; x < SpectrogramImageWidth; x++)
        {
            var columnIndex = Math.Min(columns - 1, (int)Math.Round((x / (double)Math.Max(1, SpectrogramImageWidth - 1)) * (columns - 1)));
            for (var y = 0; y < height; y++)
            {
                var rowIndex = rows - 1 - Math.Min(rows - 1, (int)Math.Round((y / (double)Math.Max(1, height - 1)) * (rows - 1)));
                image[x, top + y] = palette(matrix[rowIndex][columnIndex]);
            }
        }
    }

    private static Rgba32 SpectrogramPalette(double intensity)
    {
        intensity = Math.Clamp(intensity, 0d, 1d);
        if (intensity <= 0d)
            return new Rgba32(13, 17, 26, 255);
        if (intensity < 0.25d)
            return Blend(new Rgba32(18, 29, 58, 255), new Rgba32(45, 83, 160, 255), (float)(intensity / 0.25d));
        if (intensity < 0.5d)
            return Blend(new Rgba32(45, 83, 160, 255), new Rgba32(62, 168, 214, 255), (float)((intensity - 0.25d) / 0.25d));
        if (intensity < 0.75d)
            return Blend(new Rgba32(62, 168, 214, 255), new Rgba32(255, 201, 90, 255), (float)((intensity - 0.5d) / 0.25d));

        return Blend(new Rgba32(255, 201, 90, 255), new Rgba32(255, 246, 229, 255), (float)((intensity - 0.75d) / 0.25d));
    }

    private static Rgba32 SpectralDeltaPalette(double intensity)
    {
        intensity = Math.Clamp(Math.Pow(intensity, 0.74d), 0d, 1d);
        if (intensity <= 0d)
            return new Rgba32(14, 16, 24, 255);
        if (intensity < 0.33d)
            return Blend(new Rgba32(29, 33, 64, 255), new Rgba32(90, 72, 178, 255), (float)(intensity / 0.33d));
        if (intensity < 0.66d)
            return Blend(new Rgba32(90, 72, 178, 255), new Rgba32(226, 104, 90, 255), (float)((intensity - 0.33d) / 0.33d));

        return Blend(new Rgba32(226, 104, 90, 255), new Rgba32(255, 234, 164, 255), (float)((intensity - 0.66d) / 0.34d));
    }

    private static double[] BuildHannWindow(int size)
    {
        var window = new double[size];
        for (var i = 0; i < size; i++)
            window[i] = 0.5d * (1d - Math.Cos((2d * Math.PI * i) / Math.Max(1, size - 1)));

        return window;
    }

    private static void DrawWaveformColumn(
        Image<Rgba32> image,
        int x,
        int centerY,
        int maxHalfHeight,
        float peakValue,
        float rmsValue,
        Rgba32 outerColor,
        Rgba32 innerColor)
    {
        var peakHeight = Math.Max(1, (int)Math.Round(maxHalfHeight * Math.Clamp(peakValue, 0f, 1f)));
        var rmsHeight = Math.Max(1, (int)Math.Round(maxHalfHeight * Math.Clamp(rmsValue, 0f, 1f)));

        FillVertical(image, x, centerY - peakHeight, centerY + peakHeight, outerColor);
        FillVertical(image, x, centerY - rmsHeight, centerY + rmsHeight, innerColor);
    }

    private static void DrawDiffColumn(
        Image<Rgba32> image,
        int x,
        int top,
        int height,
        double diff,
        bool isChanged)
    {
        var normalized = Math.Clamp(Math.Pow(diff, 0.68d), 0d, 1d);
        var color = normalized <= 0d
            ? new Rgba32(26, 31, 42, 255)
            : HeatColor(normalized, isChanged);

        for (var y = top; y < top + height; y++)
            image[x, y] = color;
    }

    private static void DrawPanelBackground(Image<Rgba32> image, int top, int height)
    {
        var color = new Rgba32(20, 26, 37, 255);
        for (var y = top; y < top + height; y++)
        {
            for (var x = 0; x < image.Width; x++)
                image[x, y] = color;
        }
    }

    private static void DrawCenterLine(Image<Rgba32> image, int y)
    {
        if (y < 0 || y >= image.Height)
            return;

        var color = new Rgba32(42, 53, 73, 255);
        for (var x = 0; x < image.Width; x++)
            image[x, y] = color;
    }

    private static void DrawBorder(Image<Rgba32> image, int top, int height, Rgba32 color)
    {
        var bottom = Math.Min(image.Height - 1, top + height - 1);
        for (var x = 0; x < image.Width; x++)
        {
            image[x, top] = color;
            image[x, bottom] = color;
        }

        for (var y = top; y <= bottom; y++)
        {
            image[0, y] = color;
            image[image.Width - 1, y] = color;
        }
    }

    private static void FillVertical(Image<Rgba32> image, int x, int startY, int endY, Rgba32 color)
    {
        var top = Math.Max(0, Math.Min(startY, endY));
        var bottom = Math.Min(image.Height - 1, Math.Max(startY, endY));
        for (var y = top; y <= bottom; y++)
            image[x, y] = color;
    }

    private static Rgba32 HeatColor(double strength, bool emphasized)
    {
        var cool = new Rgba32(52, 88, 171, 255);
        var warm = emphasized
            ? new Rgba32(255, 106, 94, 255)
            : new Rgba32(255, 196, 92, 255);
        return Blend(cool, warm, (float)strength);
    }

    private static Rgba32 Blend(Rgba32 from, Rgba32 to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return new Rgba32(
            (byte)(from.R + ((to.R - from.R) * amount)),
            (byte)(from.G + ((to.G - from.G) * amount)),
            (byte)(from.B + ((to.B - from.B) * amount)),
            255);
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed record AudioSignalAnalysis(
        AudioChannelAnalysis[] Channels,
        float[] MonoSamples,
        double DurationSeconds,
        int SampleRate,
        int OriginalChannelCount,
        int DisplayChannelCount,
        double PeakAmplitude,
        double RmsAmplitude,
        double? StereoCorrelation);

    private sealed record AudioChannelAnalysis(
        int ChannelIndex,
        string ChannelDisplayName,
        float[] Peaks,
        float[] Rms,
        double PeakAmplitude,
        double RmsAmplitude);

    private sealed record AudioChannelDiffSeries(
        int ChannelIndex,
        string ChannelDisplayName,
        double BaselinePeakAmplitude,
        double CurrentPeakAmplitude,
        double BaselineRmsAmplitude,
        double CurrentRmsAmplitude,
        double SimilarityRatio,
        double ChangedTimeRatio,
        double[] DiffStrength);
}
