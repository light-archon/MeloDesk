using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeloDesk.Music;

public sealed record OnlineAlbum(string Id, string Name, string Artist, string? Thumbnail, string Year, string Type);
public sealed record CatalogPage(string OwnerId, IReadOnlyList<OnlineTrack> Tracks, IReadOnlyList<OnlineAlbum> Albums, string? Continuation);
public sealed record AlbumProfile(string Id, string Name, string Artist, string? Thumbnail, string Year,
    IReadOnlyList<OnlineTrack> Tracks, string? Continuation);

public sealed partial class YouTubeMusicCatalog
{
    private sealed record BrowseRequest(string BrowseId, string? Params = null, string? Token = null);
    private sealed record PageCursor(string OwnerId, string Kind, IReadOnlyList<BrowseRequest> Requests);

    private async Task<CatalogPage> LoadArtistSongsAsync(JsonElement root, string id, CancellationToken token)
    {
        var shelf = Find(root, "musicShelfRenderer").FirstOrDefault();
        var endpoint = Find(Path(shelf, "title"), "browseEndpoint").FirstOrDefault();
        var browseId = Text(Path(endpoint, "browseId"));
        if (browseId.Length == 0) return new(id, ParseTracks(shelf.ValueKind == JsonValueKind.Object ? shelf : root), [], null);
        return await ReadPageAsync(id, "tracks", [new(browseId, Text(Path(endpoint, "params")))], token).ConfigureAwait(false);
    }

    private async Task<CatalogPage> LoadArtistAlbumsAsync(JsonElement root, string id, CancellationToken token)
    {
        var shelves = Find(root, "musicCarouselShelfRenderer").Where(s =>
        {
            var title = Text(Path(s, "header", "musicCarouselShelfBasicHeaderRenderer", "title"));
            return title is "Albums" or "Singles & EPs";
        }).ToArray();
        var requests = shelves.Select(s => Find(Path(s, "header"), "browseEndpoint").FirstOrDefault())
            .Where(e => Text(Path(e, "browseId")).Length > 0)
            .Select(e => new BrowseRequest(Text(Path(e, "browseId")), Text(Path(e, "params")))).Distinct().ToArray();
        if (requests.Length == 0) return new(id, [], shelves.SelectMany(ParseAlbums).DistinctBy(a => a.Id).ToArray(), null);
        return await ReadPageAsync(id, "albums", requests, token).ConfigureAwait(false);
    }

    public async Task<CatalogPage> GetArtistPageAsync(string id, string kind, string continuation, CancellationToken token = default)
    {
        if (!ValidArtistId(id) || kind is not ("tracks" or "albums")) throw new ArgumentException("Некорректная страница каталога.");
        return await ContinuePageAsync(id, kind, continuation, token).ConfigureAwait(false);
    }

    public async Task<AlbumProfile> GetAlbumAsync(string id, CancellationToken token = default)
    {
        if (!Regex.IsMatch(id, "^MPRE[A-Za-z0-9_-]{3,120}$")) throw new ArgumentException("Некорректный идентификатор альбома.");
        if (Cached<AlbumProfile>("album:" + id) is { } cached) return cached;
        using var json = await RequestAsync("browse", new() { ["browseId"] = id }, token).ConfigureAwait(false);
        var header = Find(json.RootElement, "musicResponsiveHeaderRenderer").FirstOrDefault();
        if (header.ValueKind != JsonValueKind.Object) header = Find(json.RootElement, "musicDetailHeaderRenderer").FirstOrDefault();
        var name = Text(Path(header, "title"));
        if (name.Length == 0) throw new InvalidOperationException("Альбом временно недоступен.");
        var tracks = ParseTracks(json.RootElement);
        var artist = Text(Path(header, "straplineTextOne"));
        if (artist.Length == 0) artist = tracks.FirstOrDefault()?.Artist ?? "";
        var year = Regex.Match(Text(Path(header, "subtitle")), @"\b(?:19|20)\d{2}\b").Value;
        var thumb = Thumbnail(header);
        var completed = tracks.Select(t => t with { Album = name, Artist = t.ArtistId == null ? artist : t.Artist, Thumbnail = t.Thumbnail ?? thumb }).ToArray();
        var requests = NextRequests(json.RootElement, new(id));
        return Remember("album:" + id, new AlbumProfile(id, name, artist, thumb, year, completed, Cursor(id, "album", requests)));
    }

    public Task<CatalogPage> GetAlbumPageAsync(string id, string continuation, CancellationToken token = default)
        => ContinuePageAsync(id, "album", continuation, token);

    public async Task<(string Name, IReadOnlyList<OnlineTrack> Tracks)> ReadPublicPlaylistAsync(string id, CancellationToken token = default)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{10,200}$")) throw new ArgumentException("Некорректный плейлист YouTube Music.");
        using var json = await RequestAsync("browse", new() { ["browseId"] = "VL" + id }, token).ConfigureAwait(false);
        var header = Find(json.RootElement, "musicResponsiveHeaderRenderer").Concat(Find(json.RootElement, "musicDetailHeaderRenderer")).FirstOrDefault();
        var name = Text(Path(header, "title"));
        var shelf = Find(json.RootElement, "musicPlaylistShelfRenderer").FirstOrDefault();
        if (shelf.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Плейлист закрыт или временно недоступен. Используй файл экспорта.");
        var tracks = ParseTracks(shelf).ToList();
        var requests = NextRequests(shelf, new("VL" + id));
        for (var page = 0; requests.Count > 0 && tracks.Count < 1000 && page < 10; page++)
        {
            var next = await ReadPageAsync(id, "genre", requests, token).ConfigureAwait(false);
            tracks.AddRange(next.Tracks);
            requests = next.Continuation != null && Cached<PageCursor>("cursor:" + next.Continuation) is { } cursor ? cursor.Requests : [];
        }
        return (name.Length > 0 ? name : "Плейлист YouTube Music", tracks.DistinctBy(t => t.Url).Take(1000).ToArray());
    }

    private async Task<CatalogPage> ContinuePageAsync(string id, string kind, string continuation, CancellationToken token)
    {
        if (continuation.Length > 64 || Cached<PageCursor>("cursor:" + continuation) is not { } cursor || cursor.OwnerId != id || cursor.Kind != kind)
            throw new ArgumentException("Страница устарела. Открой каталог ещё раз.");
        return await ReadPageAsync(id, kind, cursor.Requests, token).ConfigureAwait(false);
    }

    private async Task<CatalogPage> ReadPageAsync(string id, string kind, IReadOnlyList<BrowseRequest> requests, CancellationToken token)
    {
        var pages = await Task.WhenAll(requests.Select(async request =>
        {
            var body = new Dictionary<string, object>();
            if (request.Token != null) body["continuation"] = request.Token;
            else { body["browseId"] = request.BrowseId; if (!string.IsNullOrEmpty(request.Params)) body["params"] = request.Params; }
            using var json = await RequestAsync("browse", body, token).ConfigureAwait(false);
            var pageRoot = json.RootElement;
            if (kind == "genre")
            {
                var shelf = Find(pageRoot, "musicPlaylistShelfRenderer").Concat(Find(pageRoot, "musicPlaylistShelfContinuation")).FirstOrDefault();
                if (shelf.ValueKind == JsonValueKind.Object) pageRoot = shelf;
            }
            return (Tracks: kind == "albums" ? Array.Empty<OnlineTrack>() : ParseTracks(pageRoot).ToArray(),
                Albums: kind == "albums" ? ParseAlbums(json.RootElement).ToArray() : Array.Empty<OnlineAlbum>(),
                Next: NextRequests(pageRoot, request));
        })).ConfigureAwait(false);
        return new(id, pages.SelectMany(p => p.Tracks).DistinctBy(t => t.Url).ToArray(),
            pages.SelectMany(p => p.Albums).DistinctBy(a => a.Id).ToArray(), Cursor(id, kind, pages.SelectMany(p => p.Next).ToArray()));
    }

    private string? Cursor(string id, string kind, IReadOnlyList<BrowseRequest> requests)
    {
        if (requests.Count == 0) return null;
        var value = Guid.NewGuid().ToString("N");
        Remember("cursor:" + value, new PageCursor(id, kind, requests));
        return value;
    }

    private static IReadOnlyList<BrowseRequest> NextRequests(JsonElement root, BrowseRequest previous)
    {
        var tokens = Find(root, "continuationItemRenderer").SelectMany(item => Find(item, "continuationCommand"))
            .Select(e => Text(Path(e, "token")))
            .Concat(Find(root, "nextContinuationData").Select(e => Text(Path(e, "continuation"))))
            .Where(t => t.Length > 0 && t != previous.Token).Distinct().ToArray();
        return tokens.Select(t => previous with { Token = t }).ToArray();
    }

    private static IEnumerable<OnlineAlbum> ParseAlbums(JsonElement root)
    {
        foreach (var row in Find(root, "musicTwoRowItemRenderer"))
        {
            if (Text(Path(row, "musicItemRendererDisplayPolicy")).Contains("GREY_OUT", StringComparison.Ordinal)) continue;
            var endpoint = Path(row, "navigationEndpoint", "browseEndpoint");
            var id = Text(Path(endpoint, "browseId"));
            if (!Regex.IsMatch(id, "^MPRE[A-Za-z0-9_-]{3,120}$")) continue;
            var title = Text(Path(row, "title"));
            if (title.Length == 0) continue;
            var subtitle = Text(Path(row, "subtitle"));
            var year = Regex.Match(subtitle, @"\b(?:19|20)\d{2}\b").Value;
            var thumbnail = Thumbnail(row.TryGetProperty("thumbnailRenderer", out var image) ? JsonSerializer.SerializeToElement(new { thumbnail = image }) : row);
            var artist = Find(Path(row, "subtitle"), "runs").Where(r => r.ValueKind == JsonValueKind.Array).SelectMany(r => r.EnumerateArray())
                .Where(r => Text(Path(r, "navigationEndpoint", "browseEndpoint", "browseId")).StartsWith("UC", StringComparison.Ordinal)).Select(r => Text(Path(r, "text"))).FirstOrDefault() ?? "";
            yield return new OnlineAlbum(id, title, artist, thumbnail, year, subtitle.Contains("Single", StringComparison.Ordinal) ? "Single" : subtitle.Contains("EP", StringComparison.Ordinal) ? "EP" : "Album");
        }
    }
}
