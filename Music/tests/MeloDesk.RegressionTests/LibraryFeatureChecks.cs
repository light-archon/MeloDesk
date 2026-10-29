using System.Net;
using System.Text;
using System.Text.Json;
using MeloDesk.Music;

internal static class LibraryFeatureChecks
{
    public static async Task FavoritesAndCreation()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "favorites-" + Guid.NewGuid().ToString("N"));
        var library = new PlaylistLibrary(42, null, root, localOnly: true);
        var track = new OnlineTrack("First", "Artist", "YouTube Music", "https://www.youtube.com/watch?v=abcdefghijk", 120);
        Check(library.Playlists.Single().IsFavorites && library.Playlists.Single().Tracks.Count == 0, "A new account has no Favorites.");
        var id = Guid.NewGuid().ToString();
        await library.ChangeAsync("create", id, "New playlist", track);
        Check(library.Playlists.Single(p => p.Id == id).Tracks.Single().Url == track.Url, "Creating a playlist lost the pending track.");
        await library.ChangeAsync("add", id, track: track);
        Check(library.Playlists.Single(p => p.Id == id).Tracks.Count == 1, "Retrying an addition duplicated the song.");
        await library.SetLikedAsync(track, true);
        await library.SetLikedAsync(track, true);
        var reopened = new PlaylistLibrary(42, null, root, localOnly: true); reopened.LoadLocal();
        Check(reopened.Playlists.Single(p => p.IsFavorites).Tracks.Single().Provider == "YouTube Music", "A liked song did not survive restart.");
        Check(reopened.Playlists.Single(p => p.Id == id).Tracks.Count == 1, "A created playlist did not survive restart.");
        var other = new PlaylistLibrary(43, null, root, localOnly: true); other.LoadLocal();
        Check(other.Playlists.Count == 1 && other.Playlists.Single().Tracks.Count == 0, "Favorites crossed accounts.");
        await reopened.SetLikedAsync(track, false);
        var final = new PlaylistLibrary(42, null, root, localOnly: true); final.LoadLocal();
        Check(final.Playlists.Single(p => p.IsFavorites).Tracks.Count == 0, "Unlike was not persisted.");
        try { await final.ChangeAsync("delete", PlaylistMerge.FavoritesId); throw new Exception("Favorites could be deleted."); }
        catch (InvalidOperationException) { }
        Check(Directory.GetParent(Path.GetFullPath(root))!.FullName == Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)), "Test cleanup escaped its root.");
        Directory.Delete(root, true);
    }

    public static async Task Discography()
    {
        using var catalog = new YouTubeMusicCatalog(new Pages());
        const string id = "UCmMUZbaYdNH0bEd1PAlAqsA";
        var artist = await catalog.GetArtistAsync(id);
        Check(artist.Tracks.Count == 50 && artist.TrackContinuation != null, "The artist is still limited to the top 20/30 songs.");
        Check(artist.Albums?.Count == 3 && artist.Albums.All(a => a.Thumbnail != null), "Album cards or artwork were lost.");
        var page = await catalog.GetArtistPageAsync(id, "tracks", artist.TrackContinuation!);
        Check(page.Tracks.Count == 40 && page.Continuation == null && page.OwnerId == id, "Continuation did not reach the remaining songs.");
        Check(artist.Tracks.Concat(page.Tracks).Select(t => t.Url).Distinct().Count() == 90, "Discography lost or duplicated songs.");
        try { await catalog.GetArtistPageAsync(id, "albums", artist.TrackContinuation!); throw new Exception("Cross-section cursor accepted."); }
        catch (ArgumentException) { }
        var album = await catalog.GetAlbumAsync("MPREb_album1");
        Check(album.Tracks.Count == 10 && album.Name == "Album 1" && album.Tracks.All(t => t.Album == "Album 1"), "Album tracks did not load.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Pages : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = document.RootElement;
            var id = body.TryGetProperty("browseId", out var b) ? b.GetString() : "";
            object data = id switch
            {
                "UCmMUZbaYdNH0bEd1PAlAqsA" => JsonSerializer.Deserialize<JsonElement>("""
                {"header":{"musicImmersiveHeaderRenderer":{"title":{"runs":[{"text":"Oasis"}]}}},"contents":[
                {"musicShelfRenderer":{"title":{"runs":[{"text":"Top songs","navigationEndpoint":{"browseEndpoint":{"browseId":"VLSongs"}}}]}}},
                {"musicCarouselShelfRenderer":{"header":{"musicCarouselShelfBasicHeaderRenderer":{"title":{"runs":[{"text":"Albums","navigationEndpoint":{"browseEndpoint":{"browseId":"MPADalbums","params":"all"}}}]}}}}}]}
                """),
                "VLSongs" => new { contents = Songs(0, 50).Concat(new object[] { new { continuationItemRenderer = new { continuationEndpoint = new { continuationCommand = new { token = "page-two" } } } } }).ToArray() },
                "MPADalbums" => new { contents = Enumerable.Range(1, 3).Select(n => new { musicTwoRowItemRenderer = new { title = Runs("Album " + n), subtitle = Runs("2025"), navigationEndpoint = new { browseEndpoint = new { browseId = "MPREb_album" + n } }, thumbnailRenderer = new { musicThumbnailRenderer = new { thumbnail = new { thumbnails = new[] { new { url = "https://example.test/album.jpg", width = 226 } } } } } } }).ToArray() },
                "MPREb_album1" => new { header = new { musicResponsiveHeaderRenderer = new { title = Runs("Album 1"), straplineTextOne = Runs("Oasis"), subtitle = Runs("Album • 2025") } }, contents = Songs(90, 10) },
                _ when body.TryGetProperty("continuation", out var c) && c.GetString() == "page-two" => new { contents = Songs(50, 40) },
                _ => new { contents = Array.Empty<object>() }
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
        }
        private static object Runs(string text) => new { runs = new[] { new { text } } };
        private static object[] Songs(int start, int count) => Enumerable.Range(start, count).Select(n => (object)new { musicResponsiveListItemRenderer = new {
            playlistItemData = new { videoId = "id" + n.ToString("D9") }, flexColumns = new[] { new { musicResponsiveListItemFlexColumnRenderer = new { text = Runs("Song " + n) } } }
        } }).ToArray();
    }
}
