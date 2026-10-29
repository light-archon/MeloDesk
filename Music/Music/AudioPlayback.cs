using LibVLCSharp.Shared;
using System.Threading.Channels;

namespace MeloDesk.Music;

public sealed class AudioPlayback : IAudioOutput
{
    private sealed class Session(long generation, MediaPlayer player)
    {
        public long Generation { get; } = generation;
        public MediaPlayer Player { get; } = player;
    }
    private readonly LibVLC _engine;
    private readonly AudioFileBuffer _buffer = new();
    private readonly AudioStreamRelay _relay = new();
    private readonly object _sync = new();
    private readonly List<Task> _cleanup = [];
    private readonly Dictionary<string, Task> _warming = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _warmSlots = new(2);
    private Session? _session;
    private long _generation;
    private int _volume = 70;
    private readonly VolumeRamp _volumeRamp = new();
    private readonly Timer _volumeTimer;
    private PlayerOptions _options = new(Bands: new float[10]);
    private bool _disposed;
    private readonly Channel<(Session Session, AudioOutputSignal Signal)> _signals = Channel.CreateUnbounded<(Session, AudioOutputSignal)>(new UnboundedChannelOptions { SingleReader = true });

    public event Action<long, AudioOutputSignal>? Signal;
    public AudioPlayback()
    {
        Core.Initialize();
        _engine = new LibVLC("--no-video", "--no-osd", "--no-media-library", "--no-volume-save");
        _engine.SetUserAgent("MeloDesk", "MeloDesk/0.9.0");
        _engine.SetAppId("org.melodesk.desktop", "0.9.0", "MeloDesk");
        _volumeTimer = new Timer(_ => AdvanceVolume(), null, Timeout.Infinite, Timeout.Infinite);
        _ = Task.Run(DispatchSignalsAsync);
    }

    public long BeginSwitch()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ++_generation;
            RetireCurrent();
            return _generation;
        }
    }

    private void RetireCurrent()
    {
        var previous = _session;
        _session = null;
        if (previous == null) return;
        // Pause only this stream; Mute changes the shared Windows audio session.
        previous.Player.SetPause(true);
        _cleanup.RemoveAll(task => task.IsCompleted);
        _cleanup.Add(Task.Run(() =>
        {
            try { previous.Player.Stop(); }
            finally { previous.Player.Dispose(); }
        }));
    }

    public bool Start(AudioStream stream, long generation)
    {
        lock (_sync)
        {
            if (_disposed || generation != _generation) return false;
            RetireCurrent();
            var player = new MediaPlayer(_engine);
            player.Volume = (int)Math.Round(_volumeRamp.ValueAt(Environment.TickCount64));
            ApplyEqualizer(player);
            var session = new Session(generation, player);
            _session = session;
            player.Playing += (_, _) => Notify(session, AudioOutputSignal.Playing);
            player.Paused += (_, _) => Notify(session, AudioOutputSignal.Paused);
            player.EndReached += (_, _) => Notify(session, AudioOutputSignal.Ended);
            player.EncounteredError += (_, _) => Notify(session, AudioOutputSignal.Error);
            using var media = CreateMedia(_buffer.GetBuffered(stream) ?? stream);
            return player.Play(media);
        }
    }
    private Media CreateMedia(AudioStream stream)
    {
        var media = new Media(_engine, new Uri(_relay.Map(stream).Url));
        media.AddOption(":network-caching=1000"); media.AddOption(":live-caching=1000");
        media.AddOption(":http-reconnect=true");
        if (stream.Headers.TryGetValue("User-Agent", out var userAgent)) media.AddOption(":http-user-agent=" + userAgent);
        if (stream.Headers.TryGetValue("Referer", out var referer)) media.AddOption(":http-referrer=" + referer);
        return media;
    }
    public Task PrepareAsync(AudioStream stream)
    {
        lock (_sync)
        {
            if (_disposed || _buffer.GetBuffered(stream) != null) return Task.CompletedTask;
            if (_warming.TryGetValue(stream.Url, out var existing)) return existing;
            return _warming[stream.Url] = Task.Run(() => PrepareCoreAsync(stream));
        }
    }
    private async Task PrepareCoreAsync(AudioStream stream)
    {
        var acquired = false;
        try
        {
            await _warmSlots.WaitAsync(_lifetime.Token); acquired = true;
            await _buffer.BufferAsync(stream, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception) { }
        finally
        {
            if (acquired) _warmSlots.Release();
            lock (_sync) _warming.Remove(stream.Url);
        }
    }
    private void Notify(Session session, AudioOutputSignal signal)
    {
        // LibVLC may call back while Play/Stop still holds native locks. Never wait or call back into the player here.
        _signals.Writer.TryWrite((session, signal));
    }
    private async Task DispatchSignalsAsync()
    {
        await foreach (var (session, signal) in _signals.Reader.ReadAllAsync())
        {
            lock (_sync) if (_disposed || !ReferenceEquals(_session, session) || _generation != session.Generation) continue;
            Signal?.Invoke(session.Generation, signal);
        }
    }

    public void SetPaused(bool paused) { lock (_sync) _session?.Player.SetPause(paused); }
    public void SetEqualizer(PlayerOptions options)
    {
        lock (_sync) { _options = options; if (_session != null) ApplyEqualizer(_session.Player); }
    }
    private void ApplyEqualizer(MediaPlayer player)
    {
        using var equalizer = new Equalizer();
        equalizer.SetPreamp(_options.EqEnabled ? _options.Preamp : 0);
        for (uint i = 0; i < 10; i++) equalizer.SetAmp(_options.EqEnabled ? _options.Bands?[i] ?? 0 : 0, i);
        player.SetEqualizer(equalizer);
    }
    public bool IsPlaying { get { lock (_sync) return _session?.Player.IsPlaying ?? false; } }
    public bool IsMuted { get { lock (_sync) return _session?.Player.Mute ?? false; } }
    public bool IsSeekable { get { lock (_sync) return _session?.Player.IsSeekable ?? false; } }
    public long Time { get { lock (_sync) return _session?.Player.Time ?? 0; } }
    public long Length { get { lock (_sync) return _session?.Player.Length ?? 0; } }
    public float Position { get { lock (_sync) return _session?.Player.Position ?? 0; } set { lock (_sync) { if (_session != null) _session.Player.Position = value; } } }
    public int Volume
    {
        get { lock (_sync) return _volume; }
        set
        {
            lock (_sync)
            {
                if (_disposed) return;
                var target = Math.Clamp(value, 0, 100);
                if (_volume == target) return;
                _volume = target;
                if (_session == null) { _volumeRamp.Reset(target); _volumeTimer.Change(Timeout.Infinite, Timeout.Infinite); }
                else { _volumeRamp.Retarget(target, Environment.TickCount64); _volumeTimer.Change(0, 15); }
            }
        }
    }
    private void AdvanceVolume()
    {
        lock (_sync)
        {
            if (_disposed) return;
            var now = Environment.TickCount64;
            if (_session != null) _session.Player.Volume = (int)Math.Round(_volumeRamp.ValueAt(now));
            if (_volumeRamp.Finished(now)) _volumeTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }
    public void Dispose()
    {
        Task[] warming;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _volumeTimer.Dispose();
            ++_generation;
            RetireCurrent();
            warming = _warming.Values.ToArray();
        }
        _lifetime.Cancel();
        _relay.Dispose();
        try { Task.WaitAll(warming); Task[] cleanup; lock (_sync) cleanup = _cleanup.ToArray(); Task.WaitAll(cleanup); }
        finally { _signals.Writer.TryComplete(); _engine.Dispose(); _buffer.Dispose(); }
    }
}
