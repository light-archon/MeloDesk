namespace MeloDesk.Music;

public sealed class TrackPlaybackController : IDisposable
{
    private sealed record Selection(long RequestId, long Generation, OnlineTrack Track, CancellationTokenSource Cancellation);
    private readonly object _sync = new();
    private readonly IAudioStreamResolver _resolver;
    private readonly IAudioOutput _output;
    private Selection? _selection;
    private PlaybackSnapshot? _current;
    private long _requestId;
    private bool _disposed;

    public event Action<PlaybackSnapshot>? Changed;
    public PlaybackSnapshot? Current { get { lock (_sync) return _current; } }
    public TrackPlaybackController(IAudioStreamResolver resolver, IAudioOutput output)
    { _resolver = resolver; _output = output; _output.Signal += OnOutputSignal; }

    public Task SelectAsync(OnlineTrack track) => SelectCoreAsync(track, null);
    public Task SelectIfCurrentAsync(OnlineTrack track, long requestId) => SelectCoreAsync(track, requestId);

    private async Task SelectCoreAsync(OnlineTrack track, long? expectedRequestId)
    {
        Selection selection;
        PlaybackSnapshot loading;
        CancellationToken cancellationToken;
        lock (_sync)
        {
            if (expectedRequestId.HasValue && (_disposed || _current?.RequestId != expectedRequestId.Value)) return;
            ObjectDisposedException.ThrowIf(_disposed, this);
            _selection?.Cancellation.Cancel();
            _selection?.Cancellation.Dispose();
            var generation = _output.BeginSwitch();
            selection = new Selection(++_requestId, generation, track, new CancellationTokenSource());
            _selection = selection;
            cancellationToken = selection.Cancellation.Token;
            _current = loading = new PlaybackSnapshot(selection.RequestId, track, TrackPlaybackState.Loading);
        }
        Changed?.Invoke(loading);
        try
        {
            var stream = await _resolver.ResolveAudioAsync(track, cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                if (_disposed || !ReferenceEquals(_selection, selection) || cancellationToken.IsCancellationRequested) return;
                if (!_output.Start(stream, selection.Generation)) throw new InvalidOperationException("Не удалось запустить аудиопоток.");
            }
            var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
            while (Current is { State: TrackPlaybackState.Loading } current && current.RequestId == selection.RequestId)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Аудиопоток не запустился. Попробуй другой результат поиска.");
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            PlaybackSnapshot? failure = null;
            lock (_sync)
                if (!_disposed && ReferenceEquals(_selection, selection))
                {
                    selection.Cancellation.Cancel();
                    _output.BeginSwitch();
                    _current = failure = new PlaybackSnapshot(selection.RequestId, track, TrackPlaybackState.Error, error.Message);
                }
            if (failure != null) { _resolver.Invalidate(track); Changed?.Invoke(failure); }
        }
    }

    private void OnOutputSignal(long generation, AudioOutputSignal signal)
    {
        PlaybackSnapshot snapshot;
        lock (_sync)
        {
            if (_disposed || _selection == null || generation != _selection.Generation) return;
            var state = signal switch
            {
                AudioOutputSignal.Playing => TrackPlaybackState.Playing,
                AudioOutputSignal.Paused => TrackPlaybackState.Paused,
                AudioOutputSignal.Ended => TrackPlaybackState.Ended,
                _ => TrackPlaybackState.Error
            };
            _current = snapshot = new PlaybackSnapshot(_selection.RequestId, _selection.Track, state,
                signal == AudioOutputSignal.Error ? "Источник прервал аудиопоток. Попробуй включить трек ещё раз." : null);
        }
        if (signal == AudioOutputSignal.Error) _resolver.Invalidate(snapshot.Track);
        Changed?.Invoke(snapshot);
    }

    public void SetPaused(bool paused) { lock (_sync) { if (!_disposed && _current?.State is TrackPlaybackState.Playing or TrackPlaybackState.Paused) _output.SetPaused(paused); } }
    public void Stop()
    {
        PlaybackSnapshot? stopped;
        lock (_sync)
        {
            if (_disposed) return;
            _selection?.Cancellation.Cancel(); _selection?.Cancellation.Dispose(); _selection = null;
            _output.BeginSwitch();
            stopped = _current == null ? null : _current with { RequestId = ++_requestId, State = TrackPlaybackState.Stopped, Error = null };
            _current = stopped;
        }
        if (stopped != null) Changed?.Invoke(stopped);
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _selection?.Cancellation.Cancel();
            _selection?.Cancellation.Dispose();
            _output.Signal -= OnOutputSignal;
        }
        _output.Dispose();
    }
}
