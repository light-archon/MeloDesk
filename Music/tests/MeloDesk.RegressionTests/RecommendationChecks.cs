using MeloDesk.Music;
using System.Net;
using System.Text;
using System.Text.Json;

internal static class RecommendationChecks
{
    private static OnlineArtist Artist(int n) => new("UC" + n.ToString("D22"), "Artist " + n, null);
    private static OnlineArtist[] Seeds => Enumerable.Range(1, 5).Select(Artist).ToArray();
    private static string Root() => Path.Combine(AppContext.BaseDirectory, "recommendations-" + Guid.NewGuid().ToString("N"));
    private static void Clean(string root)
    {
        if (Directory.GetParent(Path.GetFullPath(root))!.FullName != Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory))) throw new Exception("Unsafe fixture path.");
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
    private static void Check(bool condition, string text) { if (!condition) throw new Exception(text); }

    public static Task Onboarding()
    {
        var root = Root();
        try
        {
            var preferences = new ArtistPreferences(1, root);
            foreach (var artist in Seeds.Take(4)) preferences.SetFavorite(artist, true);
            try { preferences.CompleteOnboarding(); throw new Exception("Four artists completed onboarding."); } catch (InvalidOperationException) { }
            Check(!preferences.OnboardingCompleted, "Incomplete selection persisted as complete.");
            preferences.SetFavorite(Seeds[4], true); preferences.CompleteOnboarding();
            Check(new ArtistPreferences(1, root).OnboardingCompleted && new ArtistPreferences(1, root).Artists.Count == 5, "Onboarding selection did not survive restart.");
            Check(!new ArtistPreferences(2, root).OnboardingCompleted && new ArtistPreferences(2, root).Artists.Count == 0, "Selection crossed accounts.");
        }
        finally { Clean(root); }
        return Task.CompletedTask;
    }

    public static async Task RankingAndCache()
    {
        var root = Root(); var catalog = new Graph();
        try
        {
            var engine = new RecommendationEngine(catalog, 1, root);
            var feed = await engine.GetAsync(Seeds);
            Check(feed.Artists.First().Id == Artist(10).Id, "Shared affinity across seeds did not outrank a single-seed match.");
            Check(feed.Artists.All(a => Seeds.All(s => s.Id != a.Id)) && feed.Artists.DistinctBy(a => a.Id).Count() == feed.Artists.Count, "Favorite/duplicate artist leaked into discovery.");
            Check(feed.Albums.Count >= 5 && feed.Albums.All(a => a.Type == "Album") && feed.Albums.GroupBy(a => a.Artist).All(g => g.Count() <= 2), "Album diversity/type rules failed.");
            var calls = catalog.Calls;
            var restored = await new RecommendationEngine(catalog, 1, root).GetAsync(Seeds.Reverse().ToArray());
            Check(restored.Cached && catalog.Calls == calls && restored.SeedKey == feed.SeedKey, "Local cache was lost or depended on selection order.");
            await new RecommendationEngine(catalog, 2, root).GetAsync(Seeds);
            Check(catalog.Calls > calls, "A second account reused another account's saved profile.");
        }
        finally { Clean(root); }
    }

    public static async Task FailureAndCancellation()
    {
        var root = Root(); var catalog = new Graph();
        try
        {
            var engine = new RecommendationEngine(catalog, 1, root);
            await engine.GetAsync(Seeds);
            catalog.Fail = true;
            var offline = await engine.GetAsync(Seeds, true);
            Check(offline.Cached && offline.Albums.Count > 0 && offline.Messages.Count > 0, "Offline mode did not keep the matching saved selection.");
            var changed = await engine.GetAsync(Seeds.Take(4).Append(Artist(6)).ToArray(), true);
            Check(!changed.Cached && changed.Albums.Count == 0 && changed.Artists.Count == 0, "Wrong-profile or invented recommendations appeared offline.");
            catalog.Fail = false;
            var retried = await new RecommendationEngine(catalog, 3, root).GetAsync(Seeds);
            Check(retried.Albums.Count > 0, "An empty failure poisoned later attempts.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await engine.GetAsync(Seeds, token: cancelled.Token); throw new Exception("Cancelled cached request completed."); } catch (OperationCanceledException) { }
            catalog.Slow = true;
            using var during = new CancellationTokenSource(20);
            try { await new RecommendationEngine(catalog, 4, root).GetAsync(Seeds, token: during.Token); throw new Exception("Cancelled generation completed."); } catch (OperationCanceledException) { }
            Check(!File.Exists(Path.Combine(root, "accounts", "4", "recommendations.dat")), "Cancelled generation persisted stale data.");
            catalog.Slow = false;
            Directory.CreateDirectory(Path.Combine(root, "accounts"));
            File.WriteAllText(Path.Combine(root, "accounts", "5"), "blocks the cache directory");
            var uncached = await new RecommendationEngine(catalog, 5, root).GetAsync(Seeds);
            Check(uncached.Albums.Count > 0 && uncached.Messages.Count > 0, "A cache write failure prevented fresh recommendations.");
        }
        finally { Clean(root); }
    }

    public static async Task Parsing()
    {
        using var catalog = new YouTubeMusicCatalog(new Browse());
        var result = await catalog.GetRecommendationArtistAsync(Seeds[0].Id);
        Check(result.Artist.Name == "Seed" && result.Related.Count == 1 && result.Related[0].Id == Artist(10).Id, "Related-artist shelf parsing mixed in unrelated shelves or invalid IDs.");
        Check(result.Albums.Count == 1 && result.Albums[0].Artist == "Seed" && result.Albums[0].Year == "2026", "Album metadata was lost or disabled records leaked.");
    }

    public static async Task Live()
    {
        var root = Root();
        try
        {
            using var catalog = new YouTubeMusicCatalog();
            var choices = await catalog.GetArtistChoicesAsync("");
            Check(choices.Count >= 5, "Live catalogue returned fewer than five starter artists.");
            foreach (var artist in choices.Take(5))
            {
                var node = await catalog.GetRecommendationArtistAsync(artist.Id);
                Console.WriteLine($"LIVE {artist.Name}: {node.Related.Count} related artists, {node.Albums.Count} albums");
            }
            var feed = await new RecommendationEngine(catalog, 99, root).GetAsync(choices.Take(5).ToArray());
            Check(feed.Artists.Count > 0 && feed.Albums.Count > 0, "Live recommendation shelves are empty.");
            Console.WriteLine($"LIVE PASS: {choices.Count} choices, {feed.Artists.Count} recommended artists, {feed.Albums.Count} albums");
        }
        finally { Clean(root); }
    }

    private sealed class Graph : IRecommendationCatalog
    {
        public bool Fail, Slow;
        public int Calls;
        public async Task<RecommendationArtist> GetRecommendationArtistAsync(string id, CancellationToken token = default)
        {
            Interlocked.Increment(ref Calls); token.ThrowIfCancellationRequested();
            if (Slow) await Task.Delay(1000, token);
            if (Fail) throw new InvalidOperationException("Offline fixture");
            var n = int.Parse(id[2..]); var artist = Artist(n);
            OnlineArtist[] related = n <= 2 ? [Artist(10), Artist(11 + n), Artist(10), Artist(2)] : [Artist(11 + n)];
            return new(artist, related, [new("MPREb_" + n + "a", "A", artist.Name, null, "2026", "Album"), new("MPREb_" + n + "b", "B", artist.Name, null, "2025", "Album"), new("MPREb_" + n + "s", "Single", artist.Name, null, "2026", "Single")]);
        }
    }
    private sealed class Browse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            object Card(string id, string title, bool disabled = false) => new { musicTwoRowItemRenderer = new {
                title = new { runs = new[] { new { text = title } } }, subtitle = new { runs = new[] { new { text = "Album · 2026" } } },
                navigationEndpoint = new { browseEndpoint = new { browseId = id } },
                musicItemRendererDisplayPolicy = disabled ? "GREY_OUT" : "" } };
            object Shelf(string title, object[] contents) => new { musicCarouselShelfRenderer = new {
                header = new { musicCarouselShelfBasicHeaderRenderer = new { title = new { runs = new[] { new { text = title } } } } }, contents } };
            var data = new { header = new { musicImmersiveHeaderRenderer = new { title = new { runs = new[] { new { text = "Seed" } } } } },
                contents = new[] { Shelf("Fans might also like", [Card(Artist(10).Id, "Related"), Card("not-an-artist", "Wrong")]),
                    Shelf("Albums", [Card("MPREb_album", "Album"), Card("MPREb_disabled", "Disabled", true)]), Shelf("Unrelated shelf", [Card(Artist(50).Id, "Wrong")]) } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") });
        }
    }
}
