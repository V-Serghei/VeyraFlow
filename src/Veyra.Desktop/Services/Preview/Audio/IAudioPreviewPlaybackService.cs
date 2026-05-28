using System;
using System.Threading;
using System.Threading.Tasks;

namespace Veyra.Desktop.Services.Preview;

public interface IAudioPreviewPlaybackService : IDisposable
{
    event EventHandler? PlaybackStateChanged;

    bool IsPlaying { get; }

    TimeSpan CurrentTime { get; }

    TimeSpan TotalTime { get; }

    Task PlayAsync(
        string path,
        TimeSpan? startTime = null,
        TimeSpan? duration = null,
        bool loop = false,
        CancellationToken ct = default);

    void Seek(TimeSpan position);

    void Stop();
}
