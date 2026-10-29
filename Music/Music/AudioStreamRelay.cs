using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MeloDesk.Music;

// Keep TLS and redirects in the system HTTP stack; LibVLC reads only loopback HTTP.
public sealed class AudioStreamRelay : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly HttpClient _http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5), AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromSeconds(20) };
    private readonly CancellationTokenSource _shutdown = new();
    private sealed class Scope
    {
        public ConcurrentDictionary<string, AudioStream> Streams { get; } = new();
        public ConcurrentDictionary<string, string> Keys { get; } = new();
    }
    private readonly ConcurrentDictionary<string, Scope> _scopes = new();
    private readonly ConcurrentQueue<string> _scopeOrder = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly string _root;

    public AudioStreamRelay()
    {
        _listener.Start();
        _root = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        _ = AcceptAsync();
    }
    public AudioStream Map(AudioStream stream)
    {
        if (!Uri.TryCreate(stream.Url, UriKind.Absolute, out var uri) || uri.IsFile) return stream;
        if (uri.Scheme is not ("http" or "https")) throw new ArgumentException("Неподдерживаемый аудиопоток.");
        var scopeId = Guid.NewGuid().ToString("N");
        var scope = new Scope(); _scopes[scopeId] = scope; _scopeOrder.Enqueue(scopeId);
        while (_scopes.Count > 32 && _scopeOrder.TryDequeue(out var old)) _scopes.TryRemove(old, out _);
        return Register(stream, scopeId, scope);
    }
    private AudioStream Register(AudioStream stream, string scopeId, Scope scope)
    {
        if (scope.Streams.Count > 4096) throw new InvalidOperationException("Плейлист содержит слишком много сегментов.");
        var key = scope.Keys.GetOrAdd(stream.Url, _ => Guid.NewGuid().ToString("N"));
        scope.Streams[key] = stream;
        return new(_root + scopeId + "/" + key, new Dictionary<string, string>(), stream.IsSegmented);
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                if (_clients.Count >= 32) { client.Dispose(); continue; }
                _clients.TryAdd(client, 0);
                _ = ServeAsync(client);
            }
        }
        catch (Exception) when (_shutdown.IsCancellationRequested) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var token = deadline.Token;
            await using var output = client.GetStream();
            using var reader = new StreamReader(output, Encoding.ASCII, false, 4096, leaveOpen: true);
            var first = (await reader.ReadLineAsync(token))?.Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var headerBytes = 0;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(token)))
            {
                if ((headerBytes += line.Length) > 16384) return;
                var colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon]] = line[(colon + 1)..].Trim();
            }
            var path = first is { Length: >= 2 } ? first[1].TrimStart('/').Split('/') : [];
            if (first is not { Length: >= 2 } || first[0] is not ("GET" or "HEAD") || path.Length != 2
                || !_scopes.TryGetValue(path[0], out var scope) || !scope.Streams.TryGetValue(path[1], out var source))
            { await WriteHeaders(output, 404, "text/plain", 0, null, token); return; }
            deadline.CancelAfter(TimeSpan.FromHours(4));
            using var request = new HttpRequestMessage(first[0] == "HEAD" ? HttpMethod.Head : HttpMethod.Get, source.Url);
            foreach (var header in source.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (headers.TryGetValue("Range", out var range)) request.Headers.TryAddWithoutValidation("Range", range);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var type = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var isPlaylist = type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                || new Uri(source.Url).AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
            if (isPlaylist && response.IsSuccessStatusCode && first[0] == "GET")
            {
                await response.Content.LoadIntoBufferAsync(2_000_000);
                var text = await response.Content.ReadAsStringAsync(token);
                var baseUri = response.RequestMessage!.RequestUri!;
                string Rewrite(string value)
                {
                    var resolved = new Uri(baseUri, value);
                    if (resolved.Scheme is not ("http" or "https")) throw new InvalidDataException("Некорректный адрес сегмента.");
                    return Register(new AudioStream(resolved.AbsoluteUri, source.Headers), path[0], scope).Url;
                }
                var rewritten = string.Join('\n', text.Split('\n').Select(item =>
                    item.StartsWith('#') ? Regex.Replace(item, "URI=\"([^\"]+)\"", match => "URI=\"" + Rewrite(match.Groups[1].Value) + "\"")
                    : string.IsNullOrWhiteSpace(item) ? item : Rewrite(item.Trim())));
                var bytes = Encoding.UTF8.GetBytes(rewritten);
                await WriteHeaders(output, 200, "application/vnd.apple.mpegurl", bytes.Length, null, token);
                await output.WriteAsync(bytes, token);
            }
            else
            {
                await WriteHeaders(output, (int)response.StatusCode, type, response.Content.Headers.ContentLength,
                    response.Content.Headers.ContentRange?.ToString(), token);
                if (first[0] == "GET")
                {
                    await using var input = await response.Content.ReadAsStreamAsync(token);
                    var buffer = new byte[65536];
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                    while (true)
                    {
                        idle.CancelAfter(TimeSpan.FromSeconds(12));
                        var count = await input.ReadAsync(buffer, idle.Token);
                        if (count == 0) break;
                        idle.CancelAfter(TimeSpan.FromSeconds(12));
                        await output.WriteAsync(buffer.AsMemory(0, count), idle.Token);
                    }
                }
            }
        }
        catch (Exception) { }
        finally { _clients.TryRemove(client, out _); client.Dispose(); }
    }
    private static Task WriteHeaders(Stream output, int status, string type, long? length, string? range, CancellationToken token)
    {
        var header = $"HTTP/1.1 {status} Response\r\nContent-Type: {type}\r\nConnection: close\r\nAccept-Ranges: bytes\r\n";
        if (length.HasValue) header += $"Content-Length: {length.Value}\r\n";
        if (range != null) header += $"Content-Range: {range}\r\n";
        return output.WriteAsync(Encoding.ASCII.GetBytes(header + "\r\n"), token).AsTask();
    }
    public void Dispose()
    {
        _shutdown.Cancel(); _listener.Stop();
        foreach (var client in _clients.Keys) client.Dispose();
        _http.Dispose(); _scopes.Clear(); _shutdown.Dispose();
    }
}
