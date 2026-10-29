using System.Net;
using System.Net.Sockets;
using System.Text;
using MeloDesk;
using MeloDesk.Music;

static class BufferChecks
{
    public static Task SessionLocation()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "session-test-" + Guid.NewGuid().ToString("N"));
        var current = Path.Combine(folder, "current"); var legacy = Path.Combine(folder, "legacy");
        var selected = AppPaths.ResolveTelegramRoot(current, legacy);
        if (selected != Path.Combine(current, "tdlib")) throw new Exception("New installation did not select its own session.");
        Directory.CreateDirectory(Path.Combine(legacy, "tdlib", "database"));
        File.WriteAllText(Path.Combine(legacy, "tdlib", "database", "td.binlog"), "fixture");
        File.WriteAllText(Path.Combine(legacy, "tdlib", "database-key.dat"), "fixture");
        if (AppPaths.ResolveTelegramRoot(current, legacy) != selected) throw new Exception("An appearing legacy directory changed the saved session.");
        var upgraded = Path.Combine(folder, "upgraded");
        if (AppPaths.ResolveTelegramRoot(upgraded, legacy) != Path.Combine(legacy, "tdlib")) throw new Exception("Upgrade did not reuse the existing session.");
        File.WriteAllText(Path.Combine(upgraded, "telegram-session-location.txt"), "invalid");
        try { AppPaths.ResolveTelegramRoot(upgraded, legacy); throw new Exception("Invalid marker silently created a new session."); }
        catch (InvalidDataException) { }
        return Task.CompletedTask;
    }

    public static async Task Download()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "buffer-test-" + Guid.NewGuid().ToString("N"));
        await using var server = new AudioTestServer(Enumerable.Range(0, 10000).Select(i => (byte)i).ToArray());
        using var buffer = new AudioFileBuffer(folder);
        var stream = new AudioStream(server.Url, new Dictionary<string, string>());
        var cached = await buffer.BufferAsync(stream, CancellationToken.None) ?? throw new Exception("HTTP file was not buffered.");
        if (!File.ReadAllBytes(new Uri(cached.Url).LocalPath).SequenceEqual(server.Bytes)) throw new Exception("Buffered file differs from source.");
        await buffer.BufferAsync(stream, CancellationToken.None);
        if (server.Requests != 1) throw new Exception("Repeated preparation downloaded the same file again.");
        if (await buffer.BufferAsync(new AudioStream(server.Url + "playlist", new Dictionary<string, string>(), true), CancellationToken.None) != null)
            throw new Exception("A segmented playlist was mistaken for a complete audio file.");
        await using var broken = new AudioTestServer(server.Bytes, truncate: true);
        try { await buffer.BufferAsync(new AudioStream(broken.Url, new Dictionary<string, string>()), CancellationToken.None); }
        catch (HttpRequestException) { }
        catch (IOException) { }
        if (buffer.GetBuffered(new AudioStream(broken.Url, new Dictionary<string, string>())) != null || Directory.EnumerateFiles(folder, "*.part").Any())
            throw new Exception("An interrupted download left a playable or temporary file.");
        foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
        Directory.Delete(folder);
    }
}

sealed class AudioTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    private readonly bool _truncate;
    private int _requests;
    public byte[] Bytes { get; }
    public int Requests => Volatile.Read(ref _requests);
    public string Url { get; }
    public AudioTestServer(byte[] bytes, bool truncate = false)
    {
        Bytes = bytes; _truncate = truncate; _listener.Start();
        Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/track";
        _run = RunAsync();
    }
    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 }) { }
                Interlocked.Increment(ref _requests);
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nContent-Length: {Bytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, _stop.Token);
                await stream.WriteAsync(Bytes.AsMemory(0, _truncate ? Bytes.Length / 2 : Bytes.Length), _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _run; _stop.Dispose(); }
}
