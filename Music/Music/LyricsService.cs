using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace MeloDesk.Music;

public sealed record TrackLyrics(string TrackUrl, string Text, string? Synced, string Source, bool Instrumental = false);
public sealed class LyricsService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly SemaphoreSlim _gate = new(1);
    private readonly ConcurrentDictionary<string, TrackLyrics> _cache = new();
    private DateTimeOffset _notBefore;
    public LyricsService() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("MeloDesk/0.14 (local desktop music player)");
    public async Task<TrackLyrics> GetAsync(OnlineTrack track, CancellationToken token)
    {
        if (_cache.TryGetValue(track.Url, out var cached)) return cached;
        if (track.IsLocal && Uri.TryCreate(track.Url, UriKind.Absolute, out var local) && local.IsFile)
        {
            var path = Path.ChangeExtension(local.LocalPath, ".lyrics.txt");
            if (File.Exists(path) && new FileInfo(path).Length <= 256 * 1024) return new(track.Url, await File.ReadAllTextAsync(path, token), null, "Локальный файл");
            return new(track.Url, "Для этого файла текст не найден.", null, "Локальный файл");
        }
        await _gate.WaitAsync(token);
        try
        {
            if (DateTimeOffset.UtcNow < _notBefore) throw new InvalidOperationException("Сервис текстов просит подождать. Попробуй позже.");
            var url = "https://lrclib.net/api/get?artist_name=" + Uri.EscapeDataString(track.Artist) + "&track_name=" + Uri.EscapeDataString(track.Title);
            if (track.Duration is >= 1 and <= 3600) url += "&duration=" + Math.Round(track.Duration).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(track.Album)) url += "&album_name=" + Uri.EscapeDataString(track.Album);
            using var response = await _http.GetAsync(url, token);
            _notBefore = DateTimeOffset.UtcNow.AddMilliseconds(300);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _notBefore = DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                throw new InvalidOperationException("Сервис текстов временно ограничил запросы. Попробуй позже.");
            }
            if (response.StatusCode == HttpStatusCode.NotFound) return new(track.Url, "Текст этой песни пока не найден.", null, "LRCLIB");
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(token);
            if (bytes.Length > 1024 * 1024) throw new InvalidOperationException("Ответ сервиса текстов слишком большой.");
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            var instrumental = root.TryGetProperty("instrumental", out var flag) && flag.ValueKind == JsonValueKind.True;
            var plain = root.TryGetProperty("plainLyrics", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            var synced = root.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var result = new TrackLyrics(track.Url, instrumental ? "Инструментальная композиция" : plain.Length > 0 ? plain : synced ?? "Текст этой песни пока не найден.", synced, "LRCLIB", instrumental);
            if (_cache.Count >= 128) _cache.Clear();
            return _cache[track.Url] = result;
        }
        finally { _gate.Release(); }
    }
    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}
