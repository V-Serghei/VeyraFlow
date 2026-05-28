using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Veyra.Desktop.Services.Preview;

public sealed class AudioPreviewPlaybackService : IAudioPreviewPlaybackService
{
    private readonly object _gate = new();
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;
    private CancellationTokenSource? _segmentPlaybackCts;
    private bool _disposed;
    private int _playbackGeneration;

    public event EventHandler? PlaybackStateChanged;

    public bool IsPlaying { get; private set; }

    public TimeSpan CurrentTime
    {
        get
        {
            lock (_gate)
                return _reader?.CurrentTime ?? TimeSpan.Zero;
        }
    }

    public TimeSpan TotalTime
    {
        get
        {
            lock (_gate)
                return _reader?.TotalTime ?? TimeSpan.Zero;
        }
    }

    public async Task PlayAsync(
        string path,
        TimeSpan? startTime = null,
        TimeSpan? duration = null,
        bool loop = false,
        CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPreviewPlaybackService));

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        ct.ThrowIfCancellationRequested();

        if (loop && duration.HasValue && duration.Value > TimeSpan.Zero)
        {
            var loopReader = new AudioFileReader(path);
            var loopStart = NormalizeStart(loopReader.TotalTime, startTime);
            var loopDuration = NormalizeDuration(loopReader.TotalTime, loopStart, duration);
            loopReader.Dispose();

            if (!loopDuration.HasValue || loopDuration.Value <= TimeSpan.Zero)
                return;

            var loopPlaybackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            int generation;
            lock (_gate)
            {
                DisposePlaybackResourcesNoLock();
                generation = unchecked(++_playbackGeneration);
                _segmentPlaybackCts = loopPlaybackCts;
                IsPlaying = true;
            }

            RaisePlaybackStateChanged();
            _ = RunLoopPlaybackAsync(path, loopStart, loopDuration.Value, loopPlaybackCts.Token, generation);
            await Task.CompletedTask;
            return;
        }

        var reader = new AudioFileReader(path);
        var output = new WaveOutEvent();
        var start = NormalizeStart(reader.TotalTime, startTime);
        var limitedDuration = NormalizeDuration(reader.TotalTime, start, duration);
        var playbackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            reader.CurrentTime = start;
            output.PlaybackStopped += OnPlaybackStopped;
            output.Init(reader);

            lock (_gate)
            {
                DisposePlaybackResourcesNoLock();
                unchecked { _playbackGeneration++; }
                _reader = reader;
                _output = output;
                _segmentPlaybackCts = playbackCts;
                IsPlaying = true;
            }

            RaisePlaybackStateChanged();
            output.Play();

            if (limitedDuration.HasValue && limitedDuration.Value > TimeSpan.Zero)
            {
                _ = StopWhenSegmentFinishesAsync(limitedDuration.Value, playbackCts.Token);
            }

            await Task.CompletedTask;
        }
        catch
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            output.Dispose();
            reader.Dispose();
            playbackCts.Dispose();
            throw;
        }
    }

    public void Seek(TimeSpan position)
    {
        if (_disposed)
            return;

        lock (_gate)
        {
            if (_reader is null)
                return;

            var total = _reader.TotalTime;
            if (total <= TimeSpan.Zero)
                return;

            _reader.CurrentTime = position <= TimeSpan.Zero
                ? TimeSpan.Zero
                : position >= total
                    ? total - TimeSpan.FromMilliseconds(1)
                    : position;
        }

        RaisePlaybackStateChanged();
    }

    private async Task RunLoopPlaybackAsync(string path, TimeSpan start, TimeSpan duration, CancellationToken ct, int generation)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var reader = new AudioFileReader(path);
                using var output = new WaveOutEvent();
                reader.CurrentTime = NormalizeStart(reader.TotalTime, start);
                output.Init(reader);
                output.Play();

                try
                {
                    await Task.Delay(duration, ct);
                }
                finally
                {
                    try
                    {
                        output.Stop();
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            CleanupAfterPlaybackStopped(null, generation);
        }
    }

    public void Stop()
    {
        if (_disposed)
            return;

        WaveOutEvent? output;
        CancellationTokenSource? loopCts;
        lock (_gate)
        {
            output = _output;
            loopCts = _segmentPlaybackCts;
            if (output is null)
            {
                loopCts?.Cancel();
                DisposePlaybackResourcesNoLock();
                if (IsPlaying)
                {
                    IsPlaying = false;
                    RaisePlaybackStateChanged();
                }

                return;
            }
        }

        try
        {
            output.Stop();
        }
        catch
        {
            CleanupAfterPlaybackStopped(output);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CleanupAfterPlaybackStopped(null);
    }

    private async Task StopWhenSegmentFinishesAsync(TimeSpan duration, CancellationToken ct)
    {
        try
        {
            await Task.Delay(duration, ct);
            if (!ct.IsCancellationRequested)
                Stop();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        CleanupAfterPlaybackStopped(sender as WaveOutEvent);
    }

    private void CleanupAfterPlaybackStopped(WaveOutEvent? expectedOutput, int? expectedGeneration = null)
    {
        bool changed;

        lock (_gate)
        {
            if (expectedGeneration.HasValue && expectedGeneration.Value != _playbackGeneration)
                return;

            if (expectedOutput is not null && _output is not null && !ReferenceEquals(expectedOutput, _output))
                return;

            changed = IsPlaying || _output is not null || _reader is not null;
            DisposePlaybackResourcesNoLock();
            IsPlaying = false;
        }

        if (changed)
            RaisePlaybackStateChanged();
    }

    private void DisposePlaybackResourcesNoLock()
    {
        _segmentPlaybackCts?.Cancel();
        _segmentPlaybackCts?.Dispose();
        _segmentPlaybackCts = null;

        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Dispose();
            _output = null;
        }

        _reader?.Dispose();
        _reader = null;
    }

    private void RaisePlaybackStateChanged()
    {
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static TimeSpan NormalizeStart(TimeSpan totalTime, TimeSpan? requestedStart)
    {
        if (!requestedStart.HasValue || requestedStart.Value <= TimeSpan.Zero)
            return TimeSpan.Zero;

        if (requestedStart.Value >= totalTime)
            return totalTime > TimeSpan.Zero ? totalTime - TimeSpan.FromMilliseconds(1) : TimeSpan.Zero;

        return requestedStart.Value;
    }

    private static TimeSpan? NormalizeDuration(TimeSpan totalTime, TimeSpan start, TimeSpan? requestedDuration)
    {
        if (!requestedDuration.HasValue || requestedDuration.Value <= TimeSpan.Zero)
            return null;

        var remaining = totalTime - start;
        if (remaining <= TimeSpan.Zero)
            return null;

        return requestedDuration.Value < remaining
            ? requestedDuration.Value
            : remaining;
    }
}
