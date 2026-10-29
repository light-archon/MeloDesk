using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using MeloDesk.Music;

static class RelayChecks
{
    public static async Task Streaming()
    {
        await using var origin = new RelayOrigin();
        using var relay = new AudioStreamRelay();
        using var http = new HttpClient();
        var mapped = relay.Map(new AudioStream(origin.Root + "track", new Dictionary<string, string> { ["X-Test-Source"] = "allowed" }));
        using var request = new HttpRequestMessage(HttpMethod.Get, mapped.Url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 5);
        using var response = await http.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.PartialContent || await response.Content.ReadAsStringAsync() != "2345"
            || response.Content.Headers.ContentRange?.ToString() != "bytes 2-5/10") throw new Exception("Range or source headers were lost.");
        var playlist = relay.Map(new AudioStream(origin.Root + "master.m3u8", new Dictionary<string, string> { ["X-Test-Source"] = "allowed" }, true));
        var text = await http.GetStringAsync(playlist.Url);
        var lines = text.Split('\n');
        var segment = lines.First(item => item.StartsWith("http://127.0.0.1:"));
        var init = System.Text.RegularExpressions.Regex.Match(text, "URI=\"([^\"]+)\"").Groups[1].Value;
        if (await http.GetStringAsync(segment) != "0123456789" || await http.GetStringAsync(init) != "0123456789")
            throw new Exception("HLS segment or init URL was not relayed.");
        if (await http.GetStringAsync(playlist.Url) != text) throw new Exception("Repeated HLS loads allocated different segment URLs.");
        var guesses = new Uri(new Uri(mapped.Url), "/arbitrary/https://example.test");
        using var missing = await http.GetAsync(guesses);
        if (missing.StatusCode != HttpStatusCode.NotFound) throw new Exception("An unregistered upstream URL was accepted.");
    }

    public static async Task LocalPlaylists()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "local-playlists-" + Guid.NewGuid().ToString("N"));
        var playlistId = Guid.NewGuid().ToString();
        var library = new PlaylistLibrary(987, null, folder, localOnly: true);
        library.LoadLocal();
        await library.ChangeAsync("create", playlistId, "Локальный плейлист");
        var track = new OnlineTrack("Example", "Artist", "YouTube", "https://www.youtube.com/watch?v=example", 123);
        await library.ChangeAsync("add", playlistId, track: track);
        await library.SyncAsync(true);
        var restored = new PlaylistLibrary(987, null, folder, localOnly: true); restored.LoadLocal();
        if (restored.Pending || restored.Playlists.Single(p => !p.IsFavorites).Tracks.Single().Url != track.Url)
            throw new Exception("A local playlist did not survive restart without a cloud provider.");
        var other = new PlaylistLibrary(654, null, folder, localOnly: true); other.LoadLocal();
        if (other.Playlists.Any(p => !p.IsFavorites || p.Tracks.Count != 0)) throw new Exception("Local playlists crossed account boundaries.");
        Directory.Delete(folder, recursive: true);
    }
}

sealed class RelayOrigin : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    public string Root { get; }
    public RelayOrigin()
    {
        _listener.Start(); Root = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/"; _run = Run();
    }
    private async Task Run()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                var path = (await reader.ReadLineAsync(_stop.Token))?.Split(' ')[1];
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
                {
                    var colon = line.IndexOf(':'); if (colon > 0) headers[line[..colon]] = line[(colon + 1)..].Trim();
                }
                var allowed = headers.GetValueOrDefault("X-Test-Source") == "allowed";
                var range = headers.ContainsKey("Range");
                var playlist = path == "/master.m3u8";
                var body = !allowed ? "denied" : playlist ? "#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:5,\nsegment.m4s\n#EXT-X-ENDLIST\n"
                    : range ? "2345" : "0123456789";
                var contentType = playlist ? "application/vnd.apple.mpegurl" : "audio/mp4";
                var status = !allowed ? 403 : range ? 206 : 200;
                var response = $"HTTP/1.1 {status} Response\r\nContent-Type: {contentType}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n";
                if (range) response += "Content-Range: bytes 2-5/10\r\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response + "\r\n" + body), _stop.Token);
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _run; _stop.Dispose(); }
}
