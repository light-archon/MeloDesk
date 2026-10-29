using System.Collections.Concurrent;

namespace MeloDesk.Music;

public sealed class StreamPreparationQueue : IDisposable
{
    private readonly SemaphoreSlim _slots = new(2);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _prepared = new();
    public void Prepare(OnlineTrack track, Func<OnlineTrack, CancellationToken, Task<AudioStream>> resolve)
    {
        if (track.IsLocal || _lifetime.IsCancellationRequested) return;
        var now = DateTimeOffset.UtcNow;
        if (_prepared.TryGetValue(track.Url, out var until) && until > now) return;
        _prepared[track.Url] = now.AddMinutes(3);
        _ = RunAsync(track, resolve);
    }
    private async Task RunAsync(OnlineTrack track, Func<OnlineTrack, CancellationToken, Task<AudioStream>> resolve)
    {
        var acquired = false;
        try { await _slots.WaitAsync(_lifetime.Token); acquired = true; await resolve(track, _lifetime.Token); }
        catch (Exception) { _prepared.TryRemove(track.Url, out _); }
        finally { if (acquired) _slots.Release(); }
    }
    public void Dispose() => _lifetime.Cancel();
}
