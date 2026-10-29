namespace MeloDesk.Music;

// Only temporary stream addresses are cached in memory, never audio files.
public sealed class StreamResolutionCache : IAudioStreamResolver, IDisposable
{
    private sealed record Cached(AudioStream Stream, DateTimeOffset ExpiresAt);
    private sealed class Job
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task<AudioStream> Task { get; set; } = null!;
        public int Consumers;
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, Cached> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Func<OnlineTrack, CancellationToken, Task<AudioStream>> _resolve;
    private readonly Func<DateTimeOffset> _now;
    private bool _disposed;

    public StreamResolutionCache(Func<OnlineTrack, CancellationToken, Task<AudioStream>> resolve, Func<DateTimeOffset>? now = null)
    { _resolve = resolve; _now = now ?? (() => DateTimeOffset.UtcNow); }

    public async Task<AudioStream> ResolveAudioAsync(OnlineTrack track, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (track.IsLocal) return new AudioStream(track.Url, new Dictionary<string, string>());
        Job job;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cache.TryGetValue(track.Url, out var cached) && cached.ExpiresAt > _now()) return cached.Stream;
            if (!_jobs.TryGetValue(track.Url, out job!) || job.Cancellation.IsCancellationRequested)
            {
                job = new Job();
                _jobs[track.Url] = job;
                var capturedJob = job;
                job.Task = Task.Run(() => ResolveCoreAsync(track, capturedJob));
            }
            job.Consumers++;
        }
        try { return await job.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            lock (_sync)
            {
                job.Consumers--;
                if (job.Consumers == 0 && !job.Task.IsCompleted)
                    try { job.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private async Task<AudioStream> ResolveCoreAsync(OnlineTrack track, Job job)
    {
        try
        {
            var stream = await _resolve(track, job.Cancellation.Token).ConfigureAwait(false);
            job.Cancellation.Token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!_disposed)
                {
                    _cache[track.Url] = new Cached(stream, Expiry(stream.Url));
                    if (_cache.Count > 24) _cache.Remove(_cache.MinBy(pair => pair.Value.ExpiresAt).Key);
                }
            }
            return stream;
        }
        finally
        {
            lock (_sync)
                if (_jobs.TryGetValue(track.Url, out var current) && ReferenceEquals(current, job)) _jobs.Remove(track.Url);
            job.Cancellation.Dispose();
        }
    }

    private DateTimeOffset Expiry(string url)
    {
        var expiry = _now().AddMinutes(5);
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            foreach (var part in uri.Query.TrimStart('?').Split('&'))
            {
                var values = part.Split('=', 2);
                if (values.Length == 2 && values[0] == "expire" && long.TryParse(values[1], out var seconds))
                {
                    var signedExpiry = DateTimeOffset.FromUnixTimeSeconds(seconds).AddSeconds(-30);
                    if (signedExpiry < expiry) expiry = signedExpiry;
                }
            }
        return expiry;
    }

    public void Invalidate(OnlineTrack track) { lock (_sync) _cache.Remove(track.Url); }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var job in _jobs.Values) try { job.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            _cache.Clear();
        }
    }
}
