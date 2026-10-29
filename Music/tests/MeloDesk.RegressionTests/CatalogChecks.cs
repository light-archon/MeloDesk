using System.Net;
using System.Text;
using System.Text.Json;
using MeloDesk.Music;

internal static class CatalogChecks
{
    public static async Task Metadata()
    {
        const string artistId = "UCmMUZbaYdNH0bEd1PAlAqsA";
        using var handler = new Handler();
        using var catalog = new YouTubeMusicCatalog(handler);
        var result = await catalog.SearchAsync("Wonderwall");
        var track = result.Tracks.Single();
        var legacy = JsonSerializer.Deserialize<OnlineTrack>("{\"Title\":\"Saved\",\"Artist\":\"Test\",\"Provider\":\"YouTube\",\"Url\":\"https://www.youtube.com/watch?v=hpSrLjc5SMs\",\"Duration\":259}");
        Check(legacy is { ArtistId: null, Plays: null, IsLocal: false }, "An older saved track no longer loads.");
        var restored = JsonSerializer.Deserialize<OnlineTrack>(JsonSerializer.Serialize(track));
        Check(restored?.Plays == track.Plays && restored?.ArtistId == track.ArtistId, "Saved track statistics or artist ID did not round-trip.");
        Check(track.Provider == "YouTube Music" && track.Artist == "Oasis" && track.ArtistId == artistId, "Music artist identity was lost.");
        Check(track.Duration == 259 && track.Album == "Morning Glory", "Duration or album was lost.");
        Check(track.Plays is { Display: "1.7B", Approximate: true, Count: null, Unit: "plays" }, "A rounded source value became a fabricated exact count.");
        Check(result.Artists?.Single().MonthlyListeners is { Display: "29.1M", Unit: "monthlyListeners" }, "Monthly audience did not retain its meaning.");
        var requests = handler.Requests;
        await catalog.SearchAsync("Wonderwall");
        Check(handler.Requests == requests, "Cached search fetched the catalogue again.");
        var stats = await catalog.GetTrackStatsAsync("hpSrLjc5SMs");
        Check(stats == track.Plays && handler.Requests == requests, "Track statistics did not use their published source value.");
        var artist = await catalog.GetArtistAsync(artistId);
        Check(artist.MonthlyListeners == null && artist.Subscribers?.Display == "4.67M", "Subscribers were substituted for missing listeners.");
        Check(artist.Tracks.Count == 1 && artist.Name == "Oasis", "Artist tracks or identity are incorrect.");
        Check(await catalog.GetTrackStatsAsync("AAAAAAAAAAA") == null, "An unavailable track received another song's statistics.");
        try { await catalog.GetArtistAsync("https://example.test/"); throw new Exception("Invalid ID accepted."); }
        catch (ArgumentException) { }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await catalog.SearchAsync("Cancelled", cancelled.Token); throw new Exception("Cancelled request completed."); }
        catch (OperationCanceledException) { }
        using var timeoutCatalog = new YouTubeMusicCatalog(new TimeoutHandler());
        try { await timeoutCatalog.SearchArtistsAsync("Timeout"); throw new Exception("A source timeout was ignored."); }
        catch (TimeoutException) { }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Handler : HttpMessageHandler
    {
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            Check(request.RequestUri!.Host == "music.youtube.com", "Catalogue used another source.");
            var browse = request.RequestUri.AbsolutePath.EndsWith("browse");
            var artists = json.RootElement.TryGetProperty("params", out var param) && param.GetString()!.Contains("IgAW");
            var data = browse ? "{\"header\":{\"musicImmersiveHeaderRenderer\":{\"title\":{\"runs\":[{\"text\":\"Oasis\"}]},\"subscriptionButton\":{\"subscribeButtonRenderer\":{\"subscriberCountText\":{\"runs\":[{\"text\":\"4.67M\"}]}}}}},\"contents\":[" + Song + "]}"
                : artists ? Artists : "{\"contents\":[" + Song + "]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new TaskCanceledException("Simulated source timeout");
    }

    private const string Song = """
    {"musicResponsiveListItemRenderer":{"playlistItemData":{"videoId":"hpSrLjc5SMs"},"flexColumns":[
      {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Wonderwall"}]}}},
      {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Oasis","navigationEndpoint":{"browseEndpoint":{"browseId":"UCmMUZbaYdNH0bEd1PAlAqsA","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ARTIST"}}}}},{"text":" • "},{"text":"Morning Glory","navigationEndpoint":{"browseEndpoint":{"browseId":"MPREb_album","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ALBUM"}}}}},{"text":" • "},{"text":"4:19"}]}}},
      {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"1.7B plays"}]}}}]}}
    """;
    private const string Artists = """
    {"contents":[{"musicResponsiveListItemRenderer":{"navigationEndpoint":{"browseEndpoint":{"browseId":"UCmMUZbaYdNH0bEd1PAlAqsA"}},"flexColumns":[{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Oasis"}]}}},{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Artist • 29.1M monthly audience"}]}}}]}}]}
    """;
}
