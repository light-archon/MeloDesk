using System.Net;
using System.Text;
using System.Text.Json;
using MeloDesk.Music;

internal static class TransferPlayerChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static OnlineTrack Track(string n) => new(n, "Artist", "YouTube Music", "https://www.youtube.com/watch?v=" + n.PadLeft(11, '0'), 120);
    private static string Fixture() { var root = Path.Combine(AppContext.BaseDirectory, "transfer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    private static void Cleanup(string root)
    {
        Check(Directory.GetParent(Path.GetFullPath(root))!.FullName == Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)), "Cleanup escaped the fixture root.");
        Directory.Delete(root, true);
    }
    public static Task Queue()
    {
        var tracks = Enumerable.Range(1, 20).Select(n => Track(n.ToString())).ToArray(); var queue = new PlaybackQueue();
        queue.Install(tracks.Concat(tracks.Take(2)), false);
        Check(queue.Tracks.Count == 20, "Queue duplicated songs.");
        Check(queue.Next(tracks[^1].Url, 1, "off", true) == null, "Repeat-off wrapped the queue.");
        Check(queue.Next(tracks[^1].Url, 1, "all", true)?.Url == tracks[0].Url, "Repeat-all did not wrap.");
        Check(queue.Next(tracks[3].Url, 1, "one", true)?.Url == tracks[3].Url, "Repeat-one did not repeat on natural end.");
        Check(queue.Next(tracks[3].Url, 1, "one", false)?.Url == tracks[4].Url, "Manual Next got trapped by repeat-one.");
        Check(queue.Next(tracks[3].Url, 1, "all", false, url => url == tracks[4].Url)?.Url == tracks[5].Url, "Queue did not skip a disliked song.");
        Check(queue.Next(tracks[3].Url, 1, "all", false, _ => true) == null, "All-disliked queue entered a loop.");
        queue.Reorder(true, tracks[3].Url, new Random(42));
        Check(queue.Tracks.First().Url == tracks[3].Url && queue.Tracks.Select(t => t.Url).Distinct().Count() == 20, "Shuffle lost songs or failed to anchor the current track.");
        var visited = new List<string> { tracks[3].Url };
        for (var i = 0; i < 19; i++) visited.Add(queue.Next(visited[^1], 1, "off", false)!.Url);
        Check(visited.Distinct().Count() == 20, "Shuffle repeated songs before the queue ended.");
        return Task.CompletedTask;
    }
    public static Task Preferences()
    {
        var root = Fixture();
        try
        {
            var prefs = new PlayerPreferences(42, root);
            prefs.Set(new(true, "one", true, -3, Enumerable.Range(0, 10).Select(n => (float)n - 5).ToArray(), 33, true));
            prefs.SetDisliked(Track("1").Url, true);
            var restarted = new PlayerPreferences(42, root); restarted.Load();
            Check(restarted.Options.Shuffle && restarted.Options.Repeat == "one" && restarted.Options.Bands![3] == -2 && restarted.Options.Volume == 33 && restarted.Options.Muted, "Player preferences did not survive restart.");
            Check(restarted.IsDisliked(Track("1").Url), "Dislike did not persist.");
            var other = new PlayerPreferences(43, root); other.Load(); Check(!other.Options.Shuffle && !other.IsDisliked(Track("1").Url), "Player preferences crossed accounts.");
            try { prefs.Set(prefs.Options with { Bands = [float.NaN] }); throw new Exception("Invalid equalizer accepted."); } catch (ArgumentException) { }
            Check(prefs.Options.Bands!.Length == 10, "Invalid update corrupted the stored equalizer.");
        }
        finally { Cleanup(root); }
        return Task.CompletedTask;
    }
    public static async Task LocalFiles()
    {
        var root = Fixture();
        try
        {
            var path = Path.Combine(root, "Fixture Artist - Local Song.wav");
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                var samples = new byte[8000 * 2];
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples.Length); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples.Length); writer.Write(samples);
            }
            var local = new LocalAudioLibrary(42, root);
            var added = await local.AddAsync([path, path]); var again = await local.AddAsync([path]);
            Check(added.Count == 1 && added[0].IsLocal && again[0].Url == added[0].Url && added[0].Artist == "Fixture Artist", "Local import lost metadata or duplicated the managed file.");
            Check(File.Exists(path) && new Uri(added[0].Url).LocalPath != path, "Local import moved or deleted the original.");
            var library = new PlaylistLibrary(42, null, root, localOnly: true);
            var id = await library.ImportAsync("Local", added);
            var restarted = new PlaylistLibrary(42, null, root, localOnly: true); restarted.LoadLocal();
            Check(restarted.Playlists.Single(p => p.Id == id).Tracks.Single().IsLocal, "Local audio did not persist in the playlist.");
            var op = new PlaylistOperation(Guid.NewGuid().ToString(), 1, "add", id, Track: added[0]);
            Check(!PlaylistMerge.Valid(op) && PlaylistMerge.Valid(op, true) && PlaylistMerge.Merge(42, [new(1, 42, "fixture", [op])]).Count == 0, "A local file leaked into cloud operations.");
            var before = restarted.Playlists.Count;
            try { await restarted.ImportAsync("Invalid", [Track("1"), Track("2") with { Url = "https://untrusted.test/song" }]); throw new Exception("Unsafe track import accepted."); } catch (ArgumentException) { }
            Check(restarted.Playlists.Count == before, "Failed batch left a partial playlist.");
        }
        finally { Cleanup(root); }
    }
    public static Task FormatsAndMatching()
    {
        var csv = "\uFEFFTrack Name;Artist Name(s);Duration (ms);Track URI\r\n\"Song, with \"\"quotes\"\"\";Artist;120000;spotify:track:xyz\r\n\"Two\nLines\";Artist;125000;\r\n";
        var records = PlaylistTransfer.ParseCsv(csv);
        Check(records.Length == 2 && records[0].Title == "Song, with \"quotes\"" && records[0].Duration == 120 && records[1].Title.Contains('\n'), "CSV quotes, newlines, delimiter or milliseconds were lost.");
        var spotify = PlaylistTransfer.ParseJson("""{"items":[{"track":{"name":"Song","artists":[{"name":"Artist"}],"duration_ms":120000}}]}""");
        var yandex = PlaylistTransfer.ParseJson("""{"result":{"tracks":[{"track":{"id":"123","title":"Song","durationMs":120000,"artists":[{"name":"Artist"}]}}]}}""");
        Check(spotify.Single().Artist == "Artist" && spotify[0].Duration == 120 && yandex.Single().Title == "Song" && yandex[0].Duration == 120 && yandex[0].SourceUrl == "https://music.yandex.ru/track/123", "Spotify or Yandex export format did not parse.");
        var entry = new TransferEntry("Song", "Artist", 120);
        Check(PlaylistTransfer.Similarity(entry, Track("1") with { Title = "Song" }) > .9, "Exact match was rejected.");
        Check(PlaylistTransfer.Similarity(entry, Track("1") with { Title = "Song Live" }) == 0, "Live version silently replaced the studio recording.");
        Check(PlaylistTransfer.Similarity(entry, Track("1") with { Title = "Song", Artist = "Different Performer" }) == 0, "A different artist was accepted.");
        return Task.CompletedTask;
    }
    public static async Task ResolveMatches()
    {
        var root = Fixture();
        try
        {
            using var catalog = new YouTubeMusicCatalog(new MatchFeed()); using var search = new OnlineMusicSearch(); using var transfer = new PlaylistTransfer(catalog, search, root);
            var path = Path.Combine(root, "matches.csv");
            await File.WriteAllTextAsync(path, "title,artist,duration\nSong,Artist,120\nSong Live,Artist,120\n");
            var preview = await transfer.PreviewAsync(path, true, default);
            var progress = 0;
            var matched = await transfer.ResolveAsync(preview.Id, (done, _) => progress = done, default);
            Check(progress == 2 && matched.Matched == 1 && matched.Entries[1].Match == null && matched.Entries[1].Candidates?.Count > 0, "Matching silently substituted a studio recording for a live version.");
            var chosen = transfer.Choose(preview.Id, 1, matched.Entries[1].Candidates![0].Url);
            Check(chosen.Matched == 2 && chosen.Entries[1].Status.Contains("вручную"), "An explicit user version choice was not retained.");
        }
        finally { Cleanup(root); }
    }
    private sealed class MatchFeed : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var data = """
                {"contents":[{"musicResponsiveListItemRenderer":{"playlistItemData":{"videoId":"abcdefghijk"},"flexColumns":[{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Song"}]}}},{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Artist","navigationEndpoint":{"browseEndpoint":{"browseId":"UCmMUZbaYdNH0bEd1PAlAqsA","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_ARTIST"}}}}},{"text":" • "},{"text":"2:00"}]}}}]}}]}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "application/json") });
        }
    }
    public static async Task PreviewCommit()
    {
        var root = Fixture();
        try
        {
            using var catalog = new YouTubeMusicCatalog(); using var search = new OnlineMusicSearch(); using var transfer = new PlaylistTransfer(catalog, search, root);
            var file = Path.Combine(root, "export.csv");
            await File.WriteAllTextAsync(file, "title,artist,url\nSong,Artist,https://www.youtube.com/watch?v=abcdefghijk\nUnresolved,Other,spotify:track:xyz\n");
            var preview = await transfer.PreviewAsync(file, true, default);
            Check(preview.Matched == 1 && preview.Unresolved == 1, "Preview guessed an unresolved version.");
            var library = new PlaylistLibrary(42, null, root, localOnly: true);
            var result = await transfer.CommitAsync(preview.Id, "Imported", library);
            Check(result.Added == 1 && result.Unresolved == 1 && library.Playlists.Single(p => p.Id == result.Id).Tracks.Count == 1, "Commit lost the unresolved count or imported unconfirmed songs.");
            Check(File.Exists(Path.Combine(root, "accounts", "42", "imports", preview.Id + ".dat")), "Unresolved import report was not preserved.");
            try { transfer.Get(preview.Id); throw new Exception("Committed preview could be imported again accidentally."); } catch (ArgumentException) { }
        }
        finally { Cleanup(root); }
    }
}
