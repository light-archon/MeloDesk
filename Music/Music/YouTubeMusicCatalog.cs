using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeloDesk.Music;

public sealed record CatalogMetric(string Display, long? Count, bool Approximate, string Unit,
    string Source, DateTimeOffset RetrievedAt);
public sealed record OnlineArtist(string Id, string Name, string? Thumbnail, CatalogMetric? MonthlyListeners = null);
public sealed record ArtistProfile(string Id, string Name, string? Thumbnail, string Description,
    CatalogMetric? MonthlyListeners, CatalogMetric? Subscribers, IReadOnlyList<OnlineTrack> Tracks,
    string Source, DateTimeOffset RetrievedAt, IReadOnlyList<OnlineAlbum>? Albums = null,
    string? TrackContinuation = null, string? AlbumContinuation = null);

public sealed partial class YouTubeMusicCatalog : IDisposable
{
    private const string Source = "YouTube Music";
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, object Value)> _cache = new();
    private readonly SemaphoreSlim _slots = new(4);
    public YouTubeMusicCatalog(HttpMessageHandler? handler = null)
    {
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(12);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/140.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Origin", "https://music.youtube.com");
    }

    public async Task<IReadOnlyList<OnlineTrack>> SearchTracksAsync(string query, CancellationToken token = default)
    {
        ValidateQuery(query);
        if (Cached<OnlineSearchResult>("search:" + query) is { } full) return full.Tracks;
        if (Cached<OnlineTrack[]>("matches:" + query) is { } cached) return cached;
        return Remember("matches:" + query, (await ReadSongsAsync(query, token).ConfigureAwait(false)).ToArray());
    }

    public async Task<OnlineSearchResult> SearchAsync(string query, CancellationToken token = default)
    {
        ValidateQuery(query);
        if (Cached<OnlineSearchResult>("search:" + query) is { } cached) return cached;
        var songs = ReadSongsAsync(query, token);
        var artists = ReadArtistsSafelyAsync(query, token);
        await Task.WhenAll(songs, artists).ConfigureAwait(false);
        var tracks = await songs.ConfigureAwait(false);
        IReadOnlyList<OnlineArtist> artistList;
        var messages = new List<string>();
        var artistResult = await artists.ConfigureAwait(false);
        artistList = artistResult.Artists.Take(4).ToArray();
        if (artistResult.Failed) messages.Add("Карточки артистов временно недоступны.");
        return Remember("search:" + query, new OnlineSearchResult(tracks, messages, artistList));
    }

    private async Task<(IReadOnlyList<OnlineArtist> Artists, bool Failed)> ReadArtistsSafelyAsync(string query, CancellationToken token)
    {
        try { return (await SearchArtistsAsync(query, token).ConfigureAwait(false), false); }
        catch (Exception) when (!token.IsCancellationRequested) { return (Array.Empty<OnlineArtist>(), true); }
    }

    private async Task<IReadOnlyList<OnlineTrack>> ReadSongsAsync(string query, CancellationToken token)
    {
        using var json = await RequestAsync("search", new() { ["query"] = query, ["params"] = "EgWKAQIIAWoKEAkQChAFEAMQBA%3D%3D" }, token).ConfigureAwait(false);
        return ParseTracks(json.RootElement);
    }

    public async Task<IReadOnlyList<OnlineArtist>> SearchArtistsAsync(string query, CancellationToken token = default)
    {
        ValidateQuery(query);
        if (Cached<OnlineArtist[]>("artists:" + query) is { } cached) return cached;
        using var json = await RequestAsync("search", new() { ["query"] = query, ["params"] = "EgWKAQIgAWoKEAkQChAFEAMQBA%3D%3D" }, token).ConfigureAwait(false);
        var artists = Find(json.RootElement, "musicResponsiveListItemRenderer").Select(row =>
        {
            var id = Text(Path(row, "navigationEndpoint", "browseEndpoint", "browseId"));
            var columns = Columns(row).ToArray();
            var name = columns.Length > 0 ? Text(columns[0]) : "";
            var audience = columns.Select(Text).FirstOrDefault(t => t.Contains("monthly audience", StringComparison.OrdinalIgnoreCase));
            return new OnlineArtist(id, name, Thumbnail(row), Metric(audience, "monthlyListeners", "monthly audience"));
        }).Where(a => ValidArtistId(a.Id) && a.Name.Length > 0).DistinctBy(a => a.Id).Take(8).ToArray();
        return Remember("artists:" + query, artists);
    }

    public async Task<IReadOnlyList<OnlineArtist>> DiscoverAsync(CancellationToken token = default)
    {
        if (Cached<OnlineArtist[]>("discover") is { } cached) return cached;
        var groups = await Task.WhenAll(new[] { "Noize MC", "Кино", "Oasis", "The Weeknd" }.Select(async query =>
        {
            try { return (await SearchArtistsAsync(query, token).ConfigureAwait(false)).Take(1).ToArray(); }
            catch (Exception) when (!token.IsCancellationRequested) { return Array.Empty<OnlineArtist>(); }
        })).ConfigureAwait(false);
        var result = groups.SelectMany(a => a).DistinctBy(a => a.Id).ToArray();
        return result.Length > 0 ? Remember("discover", result) : result;
    }

    public async Task<ArtistProfile> GetArtistAsync(string id, CancellationToken token = default)
    {
        if (!ValidArtistId(id)) throw new ArgumentException("Некорректный идентификатор артиста.");
        if (Cached<ArtistProfile>("artist:" + id) is { } cached) return cached;
        using var json = await RequestAsync("browse", new() { ["browseId"] = id }, token).ConfigureAwait(false);
        var header = Find(json.RootElement, "musicImmersiveHeaderRenderer").FirstOrDefault();
        if (header.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Страница артиста временно недоступна.");
        var name = Text(Path(header, "title"));
        var audience = Metric(Text(Path(header, "monthlyListenerCount")), "monthlyListeners", "monthly audience");
        var subscribers = Metric(Text(Path(header, "subscriptionButton", "subscribeButtonRenderer", "subscriberCountText")), "subscribers", "subscribers");
        var shelf = Find(json.RootElement, "musicDescriptionShelfRenderer").FirstOrDefault();
        var description = Text(Path(shelf, "description"));
        var trackTask = LoadArtistSongsAsync(json.RootElement, id, token);
        var albumTask = LoadArtistAlbumsAsync(json.RootElement, id, token);
        await Task.WhenAll(trackTask, albumTask).ConfigureAwait(false);
        var trackPage = await trackTask.ConfigureAwait(false);
        var albumPage = await albumTask.ConfigureAwait(false);
        var card = _cache.Values.Select(item => item.Value).OfType<OnlineArtist[]>().SelectMany(items => items).FirstOrDefault(a => a.Id == id);
        if (card == null && name.Length > 0)
        {
            var cards = await ReadArtistsSafelyAsync(name, token).ConfigureAwait(false);
            card = cards.Artists.FirstOrDefault(a => a.Id == id);
        }
        return Remember("artist:" + id, new ArtistProfile(id, name, card?.Thumbnail ?? Thumbnail(header), description,
            audience, subscribers, trackPage.Tracks, Source, DateTimeOffset.UtcNow,
            albumPage.Albums, trackPage.Continuation, albumPage.Continuation));
    }

    public async Task<CatalogMetric?> GetTrackStatsAsync(string videoId, CancellationToken token = default)
    {
        if (!Regex.IsMatch(videoId, "^[A-Za-z0-9_-]{11}$")) throw new ArgumentException("Некорректный идентификатор трека.");
        if (Cached<OnlineTrack>("track:" + videoId) is { } cached && cached.Plays != null) return cached.Plays;
        using var json = await RequestAsync("player", new() { ["videoId"] = videoId }, token).ConfigureAwait(false);
        var title = Text(Path(json.RootElement, "videoDetails", "title"));
        var author = Text(Path(json.RootElement, "videoDetails", "author"));
        if (title.Length == 0) return null;
        var tracks = await ReadSongsAsync((title + " " + author).Trim(), token).ConfigureAwait(false);
        return tracks.FirstOrDefault(t => t.Url.EndsWith("=" + videoId, StringComparison.Ordinal))?.Plays;
    }

    private async Task<JsonDocument> RequestAsync(string endpoint, Dictionary<string, object> body, CancellationToken token)
    {
        body["context"] = new { client = new { clientName = "WEB_REMIX", clientVersion = "1.20261026.01.00", hl = "en", gl = "US" } };
        await _slots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var response = await _http.PostAsJsonAsync("https://music.youtube.com/youtubei/v1/" + endpoint + "?prettyPrint=false", body, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            if (bytes.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Ответ каталога слишком большой.");
            var document = JsonDocument.Parse(bytes);
            if (document.RootElement.TryGetProperty("error", out _)) { document.Dispose(); throw new InvalidOperationException("YouTube Music временно недоступен."); }
            return document;
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new TimeoutException("YouTube Music отвечает слишком долго. Попробуй ещё раз.", error); }
        catch (HttpRequestException error) { throw new InvalidOperationException("Не удалось связаться с YouTube Music. Проверь подключение.", error); }
        finally { _slots.Release(); }
    }

    public IReadOnlyList<OnlineTrack> ParseTracks(JsonElement root)
    {
        var tracks = new List<OnlineTrack>();
        foreach (var row in Find(root, "musicResponsiveListItemRenderer"))
        {
            if (Text(Path(row, "musicItemRendererDisplayPolicy")).Contains("GREY_OUT", StringComparison.Ordinal)) continue;
            var columns = Columns(row).ToArray();
            if (columns.Length == 0) continue;
            var videoId = Text(Path(row, "playlistItemData", "videoId"));
            if (videoId.Length == 0) videoId = Find(columns[0], "watchEndpoint").Select(e => Text(Path(e, "videoId"))).FirstOrDefault() ?? "";
            if (!Regex.IsMatch(videoId, "^[A-Za-z0-9_-]{11}$")) continue;
            var artists = Find(row.TryGetProperty("flexColumns", out var flex) ? flex : default, "runs")
                .Where(e => e.ValueKind == JsonValueKind.Array).SelectMany(e => e.EnumerateArray())
                .Select(run => new { Name = Text(Path(run, "text")), Id = Text(Path(run, "navigationEndpoint", "browseEndpoint", "browseId")), Page = Text(Path(run, "navigationEndpoint", "browseEndpoint", "browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig", "pageType")) }).ToArray();
            var artist = artists.FirstOrDefault(a => a.Page == "MUSIC_PAGE_TYPE_ARTIST" && ValidArtistId(a.Id));
            var album = artists.FirstOrDefault(a => a.Page == "MUSIC_PAGE_TYPE_ALBUM");
            var texts = columns.SelectMany(c => Find(c, "text")).Where(e => e.ValueKind == JsonValueKind.String).Select(Text).ToArray();
            var duration = texts.Select(ParseDuration).FirstOrDefault(d => d > 0);
            if (duration == 0) duration = Find(row, "musicResponsiveListItemFixedColumnRenderer").Select(e => ParseDuration(Text(Path(e, "text")))).FirstOrDefault(d => d > 0);
            var plays = Metric(texts.FirstOrDefault(t => t.EndsWith(" plays", StringComparison.Ordinal)), "plays", "plays");
            var track = new OnlineTrack(Text(columns[0]), artist?.Name ?? "Неизвестный исполнитель", Source,
                "https://www.youtube.com/watch?v=" + videoId, duration, Thumbnail(row), false, artist?.Id, album?.Name, plays);
            tracks.Add(track);
            Remember("track:" + videoId, track);
        }
        return tracks.DistinctBy(t => t.Url).ToArray();
    }

    private T? Cached<T>(string key) where T : class => _cache.TryGetValue(key, out var item) && DateTimeOffset.UtcNow - item.At < TimeSpan.FromMinutes(15) ? item.Value as T : null;
    private T Remember<T>(string key, T value) where T : class
    {
        if (_cache.Count >= 256) foreach (var old in _cache.OrderBy(p => p.Value.At).Take(64)) _cache.TryRemove(old.Key, out _);
        _cache[key] = (DateTimeOffset.UtcNow, value); return value;
    }
    private static void ValidateQuery(string query) { if (string.IsNullOrWhiteSpace(query) || query.Length > 500) throw new ArgumentException("Введи название трека или артиста."); }
    private static bool ValidArtistId(string id) => Regex.IsMatch(id, "^UC[A-Za-z0-9_-]{22}$");
    private static IEnumerable<JsonElement> Columns(JsonElement row) => Path(row, "flexColumns").ValueKind == JsonValueKind.Array ? Path(row, "flexColumns").EnumerateArray().Select(c => Path(c, "musicResponsiveListItemFlexColumnRenderer", "text")) : [];
    private static JsonElement Path(JsonElement value, params string[] keys) { foreach (var key in keys) { if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value)) return default; } return value; }
    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ValueKind == JsonValueKind.Object ? (value.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array ? string.Concat(runs.EnumerateArray().Select(r => Text(Path(r, "text")))) : Text(Path(value, "simpleText"))) : "";
    private static IEnumerable<JsonElement> Find(JsonElement value, string key)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) { if (property.Name == key) yield return property.Value; foreach (var item in Find(property.Value, key)) yield return item; }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) foreach (var item in Find(child, key)) yield return item;
    }
    private static string? Thumbnail(JsonElement value) => Find(Path(value, "thumbnail"), "thumbnails").Where(e => e.ValueKind == JsonValueKind.Array).SelectMany(e => e.EnumerateArray()).OrderBy(e => Path(e, "width").TryGetInt32Safe()).Select(e => Text(Path(e, "url"))).LastOrDefault(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme == "https");
    private static double ParseDuration(string value) { if (!Regex.IsMatch(value, @"^\d{1,3}:\d{2}(:\d{2})?$")) return 0; return value.Split(':').Aggregate(0d, (total, part) => total * 60 + int.Parse(part, CultureInfo.InvariantCulture)); }
    private static CatalogMetric? Metric(string? text, string unit, string suffix)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var display = text.Replace("Artist • ", "", StringComparison.Ordinal).Replace(suffix, "", StringComparison.OrdinalIgnoreCase).Trim();
        var match = Regex.Match(display, @"^[\d,.]+(?:[KMB])?$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var approximate = Regex.IsMatch(display, "[KMB]$", RegexOptions.IgnoreCase);
        long? count = !approximate && long.TryParse(display.Replace(",", ""), out var exact) ? exact : null;
        return new CatalogMetric(display, count, approximate, unit, Source, DateTimeOffset.UtcNow);
    }
    public void Dispose() { _http.Dispose(); _slots.Dispose(); }
}

internal static class CatalogJsonExtensions
{
    public static int TryGetInt32Safe(this JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
}
