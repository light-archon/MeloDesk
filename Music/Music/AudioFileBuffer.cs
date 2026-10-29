using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace MeloDesk.Music;

public sealed class AudioFileBuffer : IDisposable
{
    private const long MaxTrackBytes = 24 * 1024 * 1024;
    private const long MaxCacheBytes = 128 * 1024 * 1024;
    private readonly string _folder;
    private readonly HttpClient _http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
        { Timeout = TimeSpan.FromSeconds(20) };

    public AudioFileBuffer(string? folder = null) => _folder = folder ?? Path.Combine(AppPaths.Root, "audio-buffer");

    public AudioStream? GetBuffered(AudioStream stream)
    {
        if (Uri.TryCreate(stream.Url, UriKind.Absolute, out var uri) && uri.IsFile) return stream;
        var path = CachePath(stream.Url);
        return File.Exists(path) && new FileInfo(path).Length is > 0 and <= MaxTrackBytes
            ? new AudioStream(new Uri(path).AbsoluteUri, new Dictionary<string, string>()) : null;
    }

    public async Task<AudioStream?> BufferAsync(AudioStream stream, CancellationToken cancellationToken)
    {
        if (GetBuffered(stream) is { } cached) return cached;
        if (stream.IsSegmented || !Uri.TryCreate(stream.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")) return null;
        Directory.CreateDirectory(_folder);
        var path = CachePath(stream.Url);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            foreach (var header in stream.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (response.Content.Headers.ContentLength > MaxTrackBytes || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    total += count;
                    if (total > MaxTrackBytes) return null;
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                }
                if (total == 0 || response.Content.Headers.ContentLength is { } expected && expected != total) return null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
            Trim(path);
            return GetBuffered(stream);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string CachePath(string url) => Path.Combine(_folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".audio");
    private void Trim(string keep)
    {
        var files = new DirectoryInfo(_folder).EnumerateFiles("*.audio").OrderBy(file => file.LastWriteTimeUtc).ToArray();
        var total = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (total <= MaxCacheBytes) break;
            if (file.FullName == keep) continue;
            try { var length = file.Length; file.Delete(); total -= length; } catch (IOException) { }
        }
    }
    public void Dispose() => _http.Dispose();
}
