using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeloDesk.Music;

public sealed record TransferEntry(string Title, string Artist, double Duration = 0, string? SourceUrl = null,
    OnlineTrack? Match = null, string Status = "Не сопоставлен", IReadOnlyList<OnlineTrack>? Candidates = null);
public sealed record TransferPreview(string Id, string Name, IReadOnlyList<TransferEntry> Entries, string Source, DateTimeOffset CreatedAt)
{
    public int Matched => Entries.Count(e => e.Match != null);
    public int Unresolved => Entries.Count - Matched;
}

public sealed class PlaylistTransfer(YouTubeMusicCatalog catalog, OnlineMusicSearch search, string? dataRoot = null, HttpMessageHandler? handler = null) : IDisposable
{
    private readonly ConcurrentDictionary<string, TransferPreview> _previews = new();
    private readonly HttpClient _http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
    private readonly SemaphoreSlim _resolveGate = new(1);
    public async Task<TransferPreview> PreviewAsync(string input, bool file, CancellationToken token)
    {
        TransferEntry[] entries;
        var name = "Перенесённый плейлист";
        var source = "Файл экспорта";
        if (file)
        {
            var info = new FileInfo(input);
            if (!info.Exists || info.Length > 8 * 1024 * 1024 || info.Extension.ToLowerInvariant() is not (".csv" or ".json")) throw new ArgumentException("Выбери CSV или JSON размером до 8 МБ.");
            var text = await File.ReadAllTextAsync(info.FullName, token);
            entries = info.Extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) ? ParseCsv(text) : ParseJson(text);
            name = Path.GetFileNameWithoutExtension(info.Name);
        }
        else
        {
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0) throw new ArgumentException("Вставь общедоступную HTTPS-ссылку на плейлист.");
            if (uri.Host is "music.youtube.com" or "www.youtube.com" or "youtube.com")
            {
                var id = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0] == "list")?[1] ?? "";
                var playlist = await catalog.ReadPublicPlaylistAsync(Uri.UnescapeDataString(id), token);
                name = playlist.Name; source = "YouTube Music";
                entries = playlist.Tracks.Select(t => new TransferEntry(t.Title, t.Artist, t.Duration, t.Url, t, "Готово")).ToArray();
            }
            else if (uri.Host == "open.spotify.com")
            {
                var id = Regex.Match(uri.AbsolutePath, @"/playlist/([A-Za-z0-9]{22})(?:/|$)").Groups[1].Value;
                if (id.Length == 0) throw new ArgumentException("Нужна ссылка на плейлист Spotify.");
                var html = await ReadPublicMetadataAsync("https://open.spotify.com/embed/playlist/" + id, token);
                var payload = Regex.Match(html, @"<script[^>]*id=""__NEXT_DATA__""[^>]*>(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
                if (payload.Length == 0) throw new InvalidOperationException("Spotify не предоставил список песен. Используй CSV/JSON-экспорт.");
                entries = ParseJson(payload); source = "Spotify"; name = "Плейлист Spotify";
            }
            else if (uri.Host is "music.yandex.ru" or "music.yandex.com")
            {
                var old = Regex.Match(uri.AbsolutePath, @"^/users/([^/]+)/playlists/(\d+)/?$");
                var modern = Regex.Match(uri.AbsolutePath, @"^/playlists/([0-9a-fA-F-]{36})/?$");
                string endpoint;
                if (old.Success) endpoint = "users/" + Uri.EscapeDataString(Uri.UnescapeDataString(old.Groups[1].Value)) + "/playlists/" + old.Groups[2].Value;
                else if (modern.Success && Guid.TryParse(modern.Groups[1].Value, out var playlistUuid)) endpoint = "playlist/" + playlistUuid;
                else throw new ArgumentException("Нужна общедоступная ссылка на плейлист Яндекс Музыки.");
                using var data = JsonDocument.Parse(await ReadPublicMetadataAsync("https://api.music.yandex.net/" + endpoint, token));
                if (!data.RootElement.TryGetProperty("result", out var playlist)) throw new InvalidOperationException("Яндекс Музыка не открыла плейлист. Используй CSV/JSON-экспорт.");
                name = Value(playlist, "title") ?? name; source = "Яндекс Музыка";
                entries = ParseJson(playlist.GetRawText());
                if (playlist.TryGetProperty("trackCount", out var count) && count.TryGetInt32(out var total) && total != entries.Length)
                    throw new InvalidOperationException("Яндекс Музыка вернула неполный список песен. Используй CSV/JSON-экспорт.");
            }
            else
            {
                using var json = await search.ReadPlaylistAsync(input, token);
                var root = json.RootElement;
                name = Value(root, "title") ?? name;
                source = "SoundCloud";
                entries = await ReadSoundCloudEntriesAsync(root, token);
            }
        }
        if (entries.Length == 0) throw new InvalidOperationException("В плейлисте не найдено доступных песен. Для закрытого плейлиста используй файл экспорта.");
        if (entries.Length > 1000) throw new ArgumentException("За один раз можно перенести до 1000 песен.");
        if (_previews.Count >= 8) foreach (var old in _previews.OrderBy(p => p.Value.CreatedAt).Take(4)) _previews.TryRemove(old.Key, out _);
        var preview = new TransferPreview(Guid.NewGuid().ToString("N"), name[..Math.Min(name.Length, 100)], entries, source, DateTimeOffset.UtcNow);
        _previews[preview.Id] = preview;
        return preview;
    }
    private async Task<string> ReadPublicMetadataAsync(string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("MeloDesk/0.14");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Источник не открыл публичный плейлист. Используй CSV/JSON-экспорт.");
        if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new InvalidOperationException("Ответ источника слишком большой.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int length;
        while ((length = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + length > 8 * 1024 * 1024) throw new InvalidOperationException("Ответ источника слишком большой.");
            output.Write(buffer, 0, length);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
    private async Task<TransferEntry[]> ReadSoundCloudEntriesAsync(JsonElement root, CancellationToken token)
    {
        if (!root.TryGetProperty("entries", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        var rows = list.EnumerateArray().Take(1001).ToArray();
        if (rows.Length > 1000) throw new ArgumentException("За один раз можно перенести до 1000 песен.");
        var entries = new TransferEntry[rows.Length];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        using var slots = new SemaphoreSlim(3);
        await Task.WhenAll(rows.Select(async (row, index) =>
        {
            var url = Value(row, "webpage_url") ?? Value(row, "url") ?? "";
            if (Uri.TryCreate(url, UriKind.Absolute, out var apiUri) && apiUri.Host is "api-v2.soundcloud.com" or "api.soundcloud.com" && Regex.IsMatch(apiUri.AbsolutePath, @"^/tracks/\d+$"))
                url = "https://api.soundcloud.com" + apiUri.AbsolutePath;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var trackUri) || trackUri.Scheme != "https" || trackUri.Host is not ("soundcloud.com" or "api.soundcloud.com") || !PlaylistMerge.IsTrackUrl(url))
                throw new InvalidOperationException("SoundCloud не предоставил полный список песен. Используй CSV/JSON-экспорт.");
            await slots.WaitAsync(deadline.Token);
            try
            {
                using var metadata = JsonDocument.Parse(await ReadPublicMetadataAsync("https://soundcloud.com/oembed?format=json&url=" + Uri.EscapeDataString(url), deadline.Token));
                var data = metadata.RootElement;
                var title = Value(data, "title") ?? throw new InvalidOperationException("SoundCloud не предоставил название песни.");
                var artist = Value(data, "author_name") ?? Value(row, "uploader") ?? "SoundCloud";
                var suffix = " by " + artist;
                if (title.EndsWith(suffix, StringComparison.Ordinal)) title = title[..^suffix.Length];
                var track = new OnlineTrack(title, artist, "SoundCloud", url, 0, Value(data, "thumbnail_url"));
                entries[index] = new(title, artist, 0, url, track, "Готово");
            }
            finally { slots.Release(); }
        }));
        return entries;
    }
    public TransferPreview Get(string id) => _previews.TryGetValue(id, out var p) && DateTimeOffset.UtcNow - p.CreatedAt < TimeSpan.FromHours(2) ? p : throw new ArgumentException("Предпросмотр устарел. Открой импорт заново.");
    public async Task<TransferPreview> ResolveAsync(string id, Action<int, int>? progress, CancellationToken token)
    {
        await _resolveGate.WaitAsync(token);
        try
        {
            var preview = Get(id); var entries = preview.Entries.ToArray();
            for (var start = 0; start < entries.Length; start += 3)
            {
                token.ThrowIfCancellationRequested();
                await Task.WhenAll(Enumerable.Range(start, Math.Min(3, entries.Length - start)).Select(async index =>
                {
                    var entry = entries[index]; if (entry.Match != null) return;
                    try
                    {
                        var found = await catalog.SearchTracksAsync(entry.Artist + " " + entry.Title, token);
                        var candidates = found.OrderByDescending(t => Similarity(entry, t)).Take(5).ToArray();
                        var best = candidates.FirstOrDefault();
                        var match = best != null && Similarity(entry, best) >= .85 ? best : null;
                        entries[index] = entry with { Match = match, Candidates = candidates, Status = match != null ? "Готово" : candidates.Length > 0 ? "Нужен выбор" : "Не найден" };
                    }
                    catch (Exception) when (!token.IsCancellationRequested) { entries[index] = entry with { Status = "Не удалось проверить" }; }
                }));
                _previews[id] = preview with { Entries = entries.ToArray() };
                progress?.Invoke(Math.Min(start + 3, entries.Length), entries.Length);
            }
            return _previews[id];
        }
        finally { _resolveGate.Release(); }
    }
    public TransferPreview Choose(string id, int index, string url)
    {
        var preview = Get(id); var entries = preview.Entries.ToArray();
        if (index < 0 || index >= entries.Length) throw new ArgumentException("Песня не найдена.");
        var match = entries[index].Candidates?.FirstOrDefault(t => t.Url == url) ?? throw new ArgumentException("Выбери одну из предложенных версий.");
        entries[index] = entries[index] with { Match = match, Status = "Выбрано вручную" };
        return _previews[id] = preview with { Entries = entries };
    }
    public async Task<(string Id, int Added, int Unresolved)> CommitAsync(string id, string name, PlaylistLibrary library)
    {
        var preview = Get(id);
        var tracks = preview.Entries.Where(e => e.Match != null).Select(e => e.Match!).ToArray();
        if (tracks.Length == 0) throw new ArgumentException("Сначала сопоставь хотя бы одну песню.");
        var folder = Path.Combine(dataRoot ?? AppPaths.Root, "accounts", library.AccountId.ToString(), "imports");
        Directory.CreateDirectory(folder);
        var data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(preview), null, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(Path.Combine(folder, id + ".dat"), data);
        var playlist = await library.ImportAsync(name, tracks);
        _previews.TryRemove(id, out _);
        return (playlist, tracks.DistinctBy(t => t.Url).Count(), preview.Unresolved);
    }
    public static double Similarity(TransferEntry entry, OnlineTrack track)
    {
        var title = Tokens(entry.Title); var found = Tokens(track.Title); var artist = Tokens(entry.Artist); var performer = Tokens(track.Artist);
        double Overlap(HashSet<string> a, HashSet<string> b) => a.Count == 0 || b.Count == 0 ? 0 : (double)a.Intersect(b).Count() / a.Union(b).Count();
        var versionWords = new[] { "live", "remix", "cover", "instrumental", "караоке", "sped", "slowed", "acoustic" };
        if (versionWords.Any(w => title.Contains(w) != found.Contains(w))) return 0;
        var a = Overlap(artist, performer); var t = Overlap(title, found);
        if (a < .5 || t < .65 || entry.Duration > 0 && track.Duration > 0 && Math.Abs(entry.Duration - track.Duration) > Math.Max(8, entry.Duration * .05)) return 0;
        return .7 * t + .3 * a;
    }
    private static HashSet<string> Tokens(string value) => Regex.Matches(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace('ё', 'е'), @"[\p{L}\p{N}]+").Select(m => m.Value).ToHashSet();
    public static TransferEntry[] ParseJson(string text)
    {
        using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
        var roots = Arrays(json.RootElement).Where(a => a.Name is "tracks" or "items" or "entries" or "trackList").Select(a => a.Value).ToArray();
        if (json.RootElement.ValueKind == JsonValueKind.Array) roots = [json.RootElement];
        var result = new List<TransferEntry>();
        foreach (var list in roots)
            foreach (var original in list.EnumerateArray())
            {
                if (original.ValueKind != JsonValueKind.Object) continue;
                var row = original.TryGetProperty("track", out var track) && track.ValueKind == JsonValueKind.Object ? track : original;
                var title = Value(row, "title") ?? Value(row, "name");
                var artist = Value(row, "artist") ?? Value(row, "artistName") ?? Value(row, "uploader") ?? Value(row, "creator") ?? Value(row, "subtitle");
                if (row.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array) artist = string.Join(", ", artists.EnumerateArray().Select(a => Value(a, "name")).Where(n => n != null));
                var url = Value(row, "webpage_url") ?? Value(row, "permalink_url") ?? Value(row, "url");
                url ??= Value(row, "uri");
                if (url == null && row.TryGetProperty("durationMs", out _) && Value(row, "id") is { } yandexId && Regex.IsMatch(yandexId, @"^\d+$")) url = "https://music.yandex.ru/track/" + yandexId;
                var duration = (row.TryGetProperty("duration_ms", out var ms) || row.TryGetProperty("durationMs", out ms)) && ms.TryGetDouble(out var milliseconds) ? milliseconds / 1000 : row.TryGetProperty("duration", out var d) && d.TryGetDouble(out var seconds) ? seconds : 0;
                if (Value(row, "uri")?.StartsWith("spotify:track:", StringComparison.Ordinal) == true && !row.TryGetProperty("duration_ms", out _)) duration /= 1000;
                Add(result, title, artist, url, duration);
            }
        return result.DistinctBy(e => (e.Title, e.Artist, e.SourceUrl)).Take(1001).ToArray();
    }
    public static TransferEntry[] ParseCsv(string text)
    {
        var first = text.Split('\n', 2)[0]; var delimiter = first.Count(c => c == ';') > first.Count(c => c == ',') ? ';' : ',';
        var rows = Csv(text.TrimStart('\uFEFF'), delimiter).ToArray();
        if (rows.Length == 0) return [];
        var columns = rows[0].Select(c => Regex.Replace(c.ToLowerInvariant(), @"[^\p{L}\p{N}]", "")).ToArray();
        int Column(params string[] aliases) => Array.FindIndex(columns, c => aliases.Contains(c));
        var title = Column("title", "name", "trackname", "songname", "название");
        var artist = Column("artist", "artists", "artistnames", "artistname", "исполнитель");
        var url = Column("url", "trackurl", "trackuri", "uri");
        var duration = Column("duration", "durationms", "durationseconds");
        if (title < 0 || artist < 0) throw new ArgumentException("В CSV нужны столбцы названия песни и исполнителя.");
        var result = new List<TransferEntry>();
        foreach (var row in rows.Skip(1))
        {
            string Cell(int index) => index >= 0 && index < row.Length ? row[index].Trim() : "";
            double.TryParse(Cell(duration), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds);
            if (duration >= 0 && columns[duration] == "durationms") seconds /= 1000;
            Add(result, Cell(title), Cell(artist), Cell(url), seconds);
        }
        return result.Take(1001).ToArray();
    }
    private static void Add(List<TransferEntry> result, string? title, string? artist, string? url, double duration)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 1000) return;
        artist = string.IsNullOrWhiteSpace(artist) ? "Неизвестный исполнитель" : artist;
        if (artist.Length > 1000 || !double.IsFinite(duration) || duration < 0) return;
        OnlineTrack? match = null;
        if (url != null && PlaylistMerge.IsTrackUrl(url)) match = new(title, artist, url.Contains("soundcloud.com", StringComparison.OrdinalIgnoreCase) ? "SoundCloud" : "YouTube Music", url, duration);
        result.Add(new(title, artist, duration, url, match, match != null ? "Готово" : "Не сопоставлен"));
    }
    private static string? Value(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static IEnumerable<(string Name, JsonElement Value)> Arrays(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object) foreach (var property in root.EnumerateObject()) { if (property.Value.ValueKind == JsonValueKind.Array) yield return (property.Name, property.Value); foreach (var a in Arrays(property.Value)) yield return a; }
        else if (root.ValueKind == JsonValueKind.Array) foreach (var row in root.EnumerateArray()) foreach (var a in Arrays(row)) yield return a;
    }
    private static IEnumerable<string[]> Csv(string text, char delimiter)
    {
        var row = new List<string>(); var cell = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else quoted = !quoted; }
            else if (!quoted && (c == delimiter || c == '\n')) { row.Add(cell.ToString().TrimEnd('\r')); cell.Clear(); if (c == '\n') { yield return row.ToArray(); row.Clear(); } }
            else cell.Append(c);
        }
        if (quoted) throw new ArgumentException("В CSV не закрыта кавычка.");
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString().TrimEnd('\r')); yield return row.ToArray(); }
    }
    public void Dispose() { _http.Dispose(); _resolveGate.Dispose(); }
}
