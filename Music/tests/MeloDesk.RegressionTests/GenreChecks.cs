using System.Net;
using System.Text;
using System.Text.Json;
using MeloDesk.Music;

internal static class GenreChecks
{
    public static async Task Catalogue()
    {
        var handler = new Feed();
        using var catalog = new YouTubeMusicCatalog(handler);
        foreach (var key in new[] { "pop", "rock", "hiphop", "electronic", "focus", "evening", "indie", "energy" })
        {
            var genre = await catalog.GetGenrePlaylistAsync(key);
            Check(genre.Key == key && genre.Tracks.Count == 55 && genre.SourcePlaylistId == "VLRDCLAKofficial", "Genre did not open the source playlist, or still returned search hits.");
            Check(genre.SourcePlaylistName == "Source genre playlist" && genre.Continuation != null, "Source attribution or playlist continuation was lost.");
            var requests = handler.Requests;
            Check(ReferenceEquals(genre, await catalog.GetGenrePlaylistAsync(key)) && handler.Requests == requests, "Genre cache was not reused.");
        }
        var before = handler.Requests;
        try { await catalog.GetGenrePlaylistAsync("https://untrusted.test"); throw new Exception("Unknown genre accepted."); }
        catch (ArgumentException) { }
        Check(handler.Requests == before, "Unknown genre caused an external request.");
    }

    public static async Task PagesAndRetry()
    {
        var handler = new Feed { Empty = true };
        using var catalog = new YouTubeMusicCatalog(handler);
        try { await catalog.GetGenrePlaylistAsync("rock"); throw new Exception("Empty playlist accepted."); }
        catch (InvalidOperationException) { }
        handler.Empty = false;
        handler.FailPreferred = true;
        var genre = await catalog.GetGenrePlaylistAsync("rock");
        Check(genre.SourcePlaylistId == "VLPLcommunity01", "An unavailable source playlist prevented fallback to another playlist of the same genre.");
        var page = await catalog.GetGenrePageAsync("rock", genre.Continuation!);
        Check(page.Tracks.Count == 20 && page.OwnerId == "rock" && page.Continuation == null, "Genre playlist pagination failed.");
        try { await catalog.GetGenrePageAsync("pop", genre.Continuation!); throw new Exception("Cross-genre cursor accepted."); }
        catch (ArgumentException) { }
        try { await catalog.GetArtistPageAsync("UCmMUZbaYdNH0bEd1PAlAqsA", "tracks", genre.Continuation!); throw new Exception("Genre cursor leaked into artist songs."); }
        catch (ArgumentException) { }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Feed : HttpMessageHandler
    {
        public bool Empty { get; set; }
        public bool FailPreferred { get; set; }
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = json.RootElement;
            if (FailPreferred && root.TryGetProperty("browseId", out var id) && id.GetString() == "VLRDCLAKofficial") return new(HttpStatusCode.ServiceUnavailable);
            object data;
            if (root.TryGetProperty("query", out _))
                data = new { contents = new[] { Card("VLPLcommunity01", "Community playlist"), Card("VLRDCLAKofficial", "Source genre playlist") } };
            else if (Empty) data = new { contents = Array.Empty<object>() };
            else if (root.TryGetProperty("continuation", out _)) data = new { contents = Songs(55, 20) };
            else data = new { musicPlaylistShelfRenderer = new { contents = Songs(0, 55).Concat(new object[] { new { continuationItemRenderer = new { continuationEndpoint = new { continuationCommand = new { token = "next-genre-page" } } } } }).ToArray() }, relatedSongs = Songs(300, 7), nextContinuationData = new { continuation = "unrelated-recommendations" } };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
        }
        private static object Card(string id, string title) => new { musicResponsiveListItemRenderer = new {
            navigationEndpoint = new { browseEndpoint = new { browseId = id } }, flexColumns = new[] { new { musicResponsiveListItemFlexColumnRenderer = new { text = new { runs = new[] { new { text = title } } } } } }
        } };
        private static object[] Songs(int start, int count) => Enumerable.Range(start, count).Select(n => (object)new { musicResponsiveListItemRenderer = new {
            playlistItemData = new { videoId = "genre" + n.ToString("D6") }, flexColumns = new[] { new { musicResponsiveListItemFlexColumnRenderer = new { text = new { runs = new[] { new { text = "Song " + n } } } } } }
        } }).ToArray();
    }
}
