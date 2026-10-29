using System.Net;
using System.Text;
using System.Text.Json;
using MeloDesk.Music;

internal static class DiscoveryChecks
{
    public static async Task ReleasesAndCache()
    {
        var handler = new Feed();
        using var catalog = new YouTubeMusicCatalog(handler);
        var discovery = await catalog.GetSearchDiscoveryAsync();
        Check(discovery.Albums.Count == 1 && discovery.Albums[0].Id == "MPREb_new_release", "Discovery mixed other shelves, duplicate or unavailable releases.");
        Check(discovery.Albums[0].Artist == "Artist" && discovery.Albums[0].Year == "" && discovery.Albums[0].Thumbnail != null, "Discovery invented a year or lost source metadata.");
        Check(discovery.Messages.Count == 0 && discovery.Source == "YouTube Music", "Discovery did not preserve provenance.");
        var requests = handler.Requests;
        Check(ReferenceEquals(discovery, await catalog.GetSearchDiscoveryAsync()) && requests == handler.Requests, "Repeated opening did not reuse the discovery cache.");
        var search = await catalog.SearchAsync("Existing query");
        Check(search.Tracks.Count == 0 && discovery.Albums.Count == 1, "Discovery contaminated normal search results.");
    }

    public static async Task FailureAndCancellation()
    {
        var handler = new Feed { FailExplore = true };
        using var catalog = new YouTubeMusicCatalog(handler);
        var unavailable = await catalog.GetSearchDiscoveryAsync();
        Check(unavailable.Albums.Count == 0 && unavailable.Messages.Count > 0, "Unavailable feed was disguised as a successful new-release list.");
        handler.FailExplore = false;
        Check((await catalog.GetSearchDiscoveryAsync()).Albums.Count == 1, "Transient failure was cached and prevented retry.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var fresh = new YouTubeMusicCatalog(new Feed());
        try { await fresh.GetSearchDiscoveryAsync(cancelled.Token); throw new Exception("Cancelled discovery completed."); }
        catch (OperationCanceledException) { }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Feed : HttpMessageHandler
    {
        public bool FailExplore { get; set; }
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var explore = json.RootElement.TryGetProperty("browseId", out var id) && id.GetString() == "FEmusic_explore";
            if (explore && FailExplore) return new(HttpStatusCode.ServiceUnavailable);
            var album = new { musicTwoRowItemRenderer = new {
                title = new { runs = new[] { new { text = "New release" } } },
                subtitle = new { runs = new object[] { new { text = "Album • " }, new { text = "Artist", navigationEndpoint = new { browseEndpoint = new { browseId = "UCmMUZbaYdNH0bEd1PAlAqsA" } } } } },
                navigationEndpoint = new { browseEndpoint = new { browseId = "MPREb_new_release" } },
                thumbnailRenderer = new { musicThumbnailRenderer = new { thumbnail = new { thumbnails = new[] { new { url = "https://example.test/cover.jpg", width = 226 } } } } }
            } };
            object Shelf(string title, object[] contents) => new { musicCarouselShelfRenderer = new {
                header = new { musicCarouselShelfBasicHeaderRenderer = new { title = new { runs = new[] { new { text = title } } } } }, contents
            } };
            var disabled = JsonSerializer.Deserialize<JsonElement>("""
                {"musicTwoRowItemRenderer":{"title":{"runs":[{"text":"Disabled"}]},"navigationEndpoint":{"browseEndpoint":{"browseId":"MPREb_disabled"}},"musicItemRendererDisplayPolicy":"MUSIC_ITEM_RENDERER_DISPLAY_POLICY_GREY_OUT"}}
                """);
            object data = explore ? new { contents = new[] { Shelf("New albums & singles", [album, album, disabled]), Shelf("Popular episodes", [new { musicTwoRowItemRenderer = new { title = new { runs = new[] { new { text = "Wrong shelf" } } }, navigationEndpoint = new { browseEndpoint = new { browseId = "MPREb_not_release" } } } }]) } } : new { contents = Array.Empty<object>() };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
        }
    }
}
