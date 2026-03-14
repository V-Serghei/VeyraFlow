using NAudio.Wave;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Veyra.Infrastructure.Data.Preview;

public sealed record AudioDiffPreviewBuildResult(
    string WaveformImagePath,
    bool IsWaveformTempFile,
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
    double ChangedTimeRatio,
    int ChangedSegmentCount,
    bool HasDurationMismatch);

public static class AudioDiffPreviewBuilder
{
    private const int BucketCount = 1400;
    private const int ImageWidth = 1400;
    private const int ImageHeight = 420;
    private const int TrackMargin = 18;
    private const int DiffBandHeight = 42;
    private const int TrackGap = 16;

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
        try
        {
            var baseline = await Task.Run(() => AnalyzeSignal(baselinePath, BucketCount, ct), ct);
            var current = await Task.Run(() => AnalyzeSignal(currentPath, BucketCount, ct), ct);
            if (baseline is null || current is null)
                return null;

            var diffStrength = BuildDiffStrength(baseline, current);
            var changedBuckets = BuildChangedBucketMask(diffStrength);
            var changedSegments = CountChangedSegments(changedBuckets);
            var similarity = Math.Clamp(1d - diffStrength.Average(), 0d, 1d);
            var changedRatio = Math.Clamp(changedBuckets.Count(x => x) / (double)changedBuckets.Length, 0d, 1d);

            var tempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "audio-diff-preview");
            Directory.CreateDirectory(tempDir);
            waveformPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.audio-diff.png");

            await RenderWaveformAsync(waveformPath, baseline, current, diffStrength, changedBuckets, ct);

            return new AudioDiffPreviewBuildResult(
                WaveformImagePath: waveformPath,
                IsWaveformTempFile: true,
                BaselineDurationSeconds: baseline.DurationSeconds,
                CurrentDurationSeconds: current.DurationSeconds,
                BaselineSampleRate: baseline.SampleRate,
                CurrentSampleRate: current.SampleRate,
                BaselineChannels: baseline.Channels,
                CurrentChannels: current.Channels,
                BaselinePeakAmplitude: baseline.PeakAmplitude,
                CurrentPeakAmplitude: current.PeakAmplitude,
                BaselineRmsAmplitude: baseline.RmsAmplitude,
                CurrentRmsAmplitude: current.RmsAmplitude,
                SignalSimilarityRatio: similarity,
                ChangedTimeRatio: changedRatio,
                ChangedSegmentCount: changedSegments,
                HasDurationMismatch: Math.Abs(baseline.DurationSeconds - current.DurationSeconds) >= 0.05d);
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(waveformPath))
                TryDelete(waveformPath);
            throw;
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(waveformPath))
                TryDelete(waveformPath);
            return null;
        }
    }

    private static AudioSignalAnalysis? AnalyzeSignal(string path, int bucketCount, CancellationToken ct)
    {
        using var reader = new AudioFileReader(path);
        var sampleRate = reader.WaveFormat.SampleRate;
        var channels = Math.Max(1, reader.WaveFormat.Channels);
        var durationSeconds = Math.Max(0d, reader.TotalTime.TotalSeconds);
        var totalFrames = Math.Max(1L, reader.Length / Math.Max(1, reader.WaveFormat.BlockAlign));

        var peaks = new float[bucketCount];
        var rmsAccumulator = new double[bucketCount];
        var bucketSampleCounts = new int[bucketCount];

        var peakAmplitude = 0d;
        var totalSquare = 0d;
        var totalSampleCount = 0L;
        var frameIndex = 0L;

        var buffer = new float[Math.Max(8192, sampleRate / 2) * channels];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();

            var framesRead = read / channels;
            for (var frame = 0; frame < framesRead; frame++)
            {
                double monoSample = 0d;
                var baseOffset = frame * channels;
                for (var ch = 0; ch < channels; ch++)
                    monoSample += buffer[baseOffset + ch];

                monoSample /= channels;

                var abs = Math.Abs(monoSample);
                peakAmplitude = Math.Max(peakAmplitude, abs);
                totalSquare += monoSample * monoSample;
                totalSampleCount++;

                var bucketIndex = totalFrames <= 1
                    ? 0
                    : (int)Math.Min(bucketCount - 1, (frameIndex * bucketCount) / totalFrames);

                peaks[bucketIndex] = Math.Max(peaks[bucketIndex], (float)abs);
                rmsAccumulator[bucketIndex] += monoSample * monoSample;
                bucketSampleCounts[bucketIndex]++;
                frameIndex++;
            }
        }

        var rms = new float[bucketCount];
        for (var i = 0; i < bucketCount; i++)
        {
            rms[i] = bucketSampleCounts[i] <= 0
                ? 0f
                : (float)Math.Sqrt(rmsAccumulator[i] / bucketSampleCounts[i]);
        }

        var totalRms = totalSampleCount <= 0
            ? 0d
            : Math.Sqrt(totalSquare / totalSampleCount);

        return new AudioSignalAnalysis(
            peaks,
            rms,
            durationSeconds,
            sampleRate,
            channels,
            peakAmplitude,
            totalRms);
    }

    private static double[] BuildDiffStrength(AudioSignalAnalysis baseline, AudioSignalAnalysis current)
    {
        var bucketCount = Math.Min(baseline.Peaks.Length, current.Peaks.Length);
        var diff = new double[bucketCount];
        for (var i = 0; i < bucketCount; i++)
        {
            var peakDelta = Math.Abs(baseline.Peaks[i] - current.Peaks[i]);
            var rmsDelta = Math.Abs(baseline.Rms[i] - current.Rms[i]);
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
        var threshold = Math.Clamp(Math.Max(0.0125d, average * 1.28d), 0.0125d, 0.09d);
        var changed = diffStrength.Select(x => x >= threshold).ToArray();

        for (var i = 1; i < changed.Length - 1; i++)
        {
            if (!changed[i] && changed[i - 1] && changed[i + 1])
                changed[i] = true;
        }

        for (var i = 1; i < changed.Length - 1; i++)
        {
            if (changed[i] && !changed[i - 1] && !changed[i + 1] && diffStrength[i] < threshold * 1.45d)
                changed[i] = false;
        }

        return changed;
    }

    private static int CountChangedSegments(bool[] changed)
    {
        var segments = 0;
        var inSegment = false;

        foreach (var isChanged in changed)
        {
            if (isChanged && !inSegment)
            {
                segments++;
                inSegment = true;
            }
            else if (!isChanged)
            {
                inSegment = false;
            }
        }

        return segments;
    }

    private static async Task RenderWaveformAsync(
        string outputPath,
        AudioSignalAnalysis baseline,
        AudioSignalAnalysis current,
        double[] diffStrength,
        bool[] changedBuckets,
        CancellationToken ct)
    {
        using var image = new Image<Rgba32>(ImageWidth, ImageHeight, new Rgba32(16, 20, 28, 255));

        var trackHeight = (ImageHeight - (TrackMargin * 2) - DiffBandHeight - TrackGap * 2) / 2;
        var topTrackTop = TrackMargin;
        var diffBandTop = topTrackTop + trackHeight + TrackGap;
        var bottomTrackTop = diffBandTop + DiffBandHeight + TrackGap;
        var usableTrackHalfHeight = Math.Max(1, (trackHeight / 2) - 6);
        var topCenterY = topTrackTop + (trackHeight / 2);
        var bottomCenterY = bottomTrackTop + (trackHeight / 2);

        DrawPanelBackground(image, topTrackTop, trackHeight);
        DrawPanelBackground(image, diffBandTop, DiffBandHeight);
        DrawPanelBackground(image, bottomTrackTop, trackHeight);
        DrawCenterLine(image, topCenterY);
        DrawCenterLine(image, bottomCenterY);

        for (var x = 0; x < ImageWidth; x++)
        {
            ct.ThrowIfCancellationRequested();

            var bucketIndex = Math.Min(baseline.Peaks.Length - 1, (int)((x / (double)Math.Max(1, ImageWidth - 1)) * (baseline.Peaks.Length - 1)));
            var diff = Math.Clamp(diffStrength[bucketIndex], 0d, 1d);
            var isChanged = changedBuckets[bucketIndex];

            var baselineColor = isChanged
                ? new Rgba32(110, 176, 255, 255)
                : new Rgba32(86, 132, 214, 255);
            var currentColor = isChanged
                ? new Rgba32(111, 235, 183, 255)
                : new Rgba32(79, 196, 146, 255);

            DrawWaveformColumn(image, x, topCenterY, usableTrackHalfHeight, baseline.Peaks[bucketIndex], baseline.Rms[bucketIndex], baselineColor);
            DrawWaveformColumn(image, x, bottomCenterY, usableTrackHalfHeight, current.Peaks[bucketIndex], current.Rms[bucketIndex], currentColor);
            DrawDiffColumn(image, x, diffBandTop, DiffBandHeight, diff, isChanged);
        }

        DrawBorder(image, topTrackTop, trackHeight, new Rgba32(54, 66, 89, 255));
        DrawBorder(image, diffBandTop, DiffBandHeight, new Rgba32(54, 66, 89, 255));
        DrawBorder(image, bottomTrackTop, trackHeight, new Rgba32(54, 66, 89, 255));

        await using var stream = File.Create(outputPath);
        await image.SaveAsPngAsync(stream, new PngEncoder(), ct);
    }

    private static void DrawWaveformColumn(
        Image<Rgba32> image,
        int x,
        int centerY,
        int maxHalfHeight,
        float peakValue,
        float rmsValue,
        Rgba32 baseColor)
    {
        var peakHeight = Math.Max(1, (int)Math.Round(maxHalfHeight * Math.Clamp(peakValue, 0f, 1f)));
        var rmsHeight = Math.Max(1, (int)Math.Round(maxHalfHeight * Math.Clamp(rmsValue, 0f, 1f)));

        var outerColor = Blend(baseColor, new Rgba32(255, 255, 255, 255), 0.12f);
        var innerColor = Blend(baseColor, new Rgba32(255, 255, 255, 255), 0.38f);

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

    private sealed record AudioSignalAnalysis(
        float[] Peaks,
        float[] Rms,
        double DurationSeconds,
        int SampleRate,
        int Channels,
        double PeakAmplitude,
        double RmsAmplitude);

    private static void TryDelete(string path)
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
}
