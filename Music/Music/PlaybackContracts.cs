namespace MeloDesk.Music;

public interface IAudioStreamResolver
{
    Task<AudioStream> ResolveAudioAsync(OnlineTrack track, CancellationToken cancellationToken = default);
    void Invalidate(OnlineTrack track);
}

public enum AudioOutputSignal { Playing, Paused, Ended, Error }
public interface IAudioOutput : IDisposable
{
    event Action<long, AudioOutputSignal>? Signal;
    long BeginSwitch();
    bool Start(AudioStream stream, long generation);
    void SetPaused(bool paused);
    bool IsPlaying { get; }
    bool IsSeekable { get; }
    long Time { get; }
    long Length { get; }
    float Position { get; set; }
    int Volume { get; set; }
}

public enum TrackPlaybackState { Loading, Playing, Paused, Ended, Error, Stopped }
public sealed record PlaybackSnapshot(long RequestId, OnlineTrack Track, TrackPlaybackState State, string? Error = null);
