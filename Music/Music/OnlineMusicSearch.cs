using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.ComponentModel;
using System.Windows.Media;
using System.Text.Json.Serialization;
using System.Net.Http;
using System.Net.Http.Headers;

namespace MeloDesk.Music;

public sealed record OnlineTrack(string Title, string Artist, string Provider, string Url, double Duration,
    string? Thumbnail = null, bool IsLocal = false, string? ArtistId = null, string? Album = null,
    CatalogMetric? Plays = null) : INotifyPropertyChanged
{
    private ImageSource? _artwork;
    [JsonIgnore] public ImageSource? Artwork { get => _artwork; set { _artwork = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Artwork))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string DurationText => Duration <= 0 ? "—" : TimeSpan.FromSeconds(Duration).ToString(Duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    public string CoverText => Provider == "YouTube" ? "▶" : "♪";
    public string CoverColor => Provider == "YouTube" ? "#98516F" : Provider == "SoundCloud" ? "#A86F4A" : "#4D7395";
}
public sealed record OnlineSearchResult(IReadOnlyList<OnlineTrack> Tracks, IReadOnlyList<string> Messages,
    IReadOnlyList<OnlineArtist>? Artists = null);
public sealed record AudioStream(string Url, IReadOnlyDictionary<string, string> Headers, bool IsSegmented = false);

public sealed class OnlineMusicSearch : IDisposable
{
    private const string AudioFormat = "bestaudio[protocol=https]/bestaudio[protocol=http]/bestaudio/best";
    private readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly string _toolsPath;
    public OnlineMusicSearch(string? toolsPath = null) => _toolsPath = toolsPath ?? Path.Combine(AppContext.BaseDirectory, "tools");

    public async Task<OnlineSearchResult> SearchAsync(string query, string source = "Все", CancellationToken cancellationToken = default, Action<IReadOnlyList<OnlineTrack>>? onBatch = null)
    {
        if (Uri.TryCreate(query, UriKind.Absolute, out var link) && link.Scheme is "http" or "https")
        {
            if (!IsSupportedLink(link)) throw new ArgumentException("Поддерживаются ссылки YouTube и SoundCloud.");
            using var document = JsonDocument.Parse(await RunAsync(new[] { "--dump-single-json", "--skip-download", "--no-playlist", "--format", "bestaudio/best", query }, cancellationToken).ConfigureAwait(false));
            return new OnlineSearchResult(new[] { ParseTrack(document.RootElement, link.Host.Contains("soundcloud") ? "SoundCloud" : "YouTube") }, Array.Empty<string>());
        }

        var providers = source == "YouTube" ? new[] { "YouTube" } : source == "SoundCloud" ? new[] { "SoundCloud" } : new[] { "YouTube", "SoundCloud" };
        var results = await Task.WhenAll(providers.Select(async provider =>
        {
            try
            {
                var search = (provider == "YouTube" ? "ytsearch8:" : "scsearch8:") + query;
                using var document = JsonDocument.Parse(await RunAsync(new[] { "--flat-playlist", "--dump-single-json", "--skip-download", search }, cancellationToken).ConfigureAwait(false));
                var entries = document.RootElement.GetProperty("entries");
                var tracks = entries.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => ParseTrack(e, provider)).ToArray();
                onBatch?.Invoke(tracks);
                return (Tracks: tracks, Error: (string?)null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { return (Tracks: Array.Empty<OnlineTrack>(), Error: $"{provider}: {error.Message}"); }
        })).ConfigureAwait(false);
        return new OnlineSearchResult(results.SelectMany(r => r.Tracks).ToArray(), results.Where(r => r.Error != null).Select(r => r.Error!).ToArray());
    }

    public async Task<AudioStream> ResolveAudioAsync(OnlineTrack track, CancellationToken cancellationToken = default)
    {
        if (track.IsLocal) return new AudioStream(track.Url, new Dictionary<string, string>());
        string json;
        if (track.Provider is "YouTube" or "YouTube Music")
        {
            try
            {
                json = await RunAsync(new[] { "--extractor-args", "youtube:player_client=android", "--dump-single-json", "--skip-download", "--no-playlist", "--format", AudioFormat, track.Url }, cancellationToken).ConfigureAwait(false);
                using var candidate = JsonDocument.Parse(json);
                using var request = new HttpRequestMessage(HttpMethod.Get, GetString(candidate.RootElement, "url"));
                request.Headers.Range = new RangeHeaderValue(0, 0);
                if (candidate.RootElement.TryGetProperty("http_headers", out var probeHeaders))
                    foreach (var header in probeHeaders.EnumerateObject())
                        if (header.Value.ValueKind == JsonValueKind.String) request.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
                using var response = await _probe.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            { json = await RunAsync(new[] { "--dump-single-json", "--skip-download", "--no-playlist", "--format", AudioFormat, track.Url }, cancellationToken).ConfigureAwait(false); }
        }
        else json = await RunAsync(new[] { "--dump-single-json", "--skip-download", "--no-playlist", "--format", AudioFormat, track.Url }, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var streamUrl = GetString(root, "url");
        if (string.IsNullOrEmpty(streamUrl)) throw new InvalidOperationException("Источник не предоставил аудиопоток.");
        var headers = new Dictionary<string, string>();
        if (root.TryGetProperty("http_headers", out var headerObject))
            foreach (var header in headerObject.EnumerateObject())
                if (header.Value.ValueKind == JsonValueKind.String) headers[header.Name] = header.Value.GetString()!;
        var protocol = GetString(root, "protocol") ?? "";
        return new AudioStream(streamUrl, headers, protocol.Contains("m3u8") || protocol.Contains("dash_segments"));
    }

    private async Task<string> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var fastExecutable = Path.Combine(_toolsPath, "yt-dlp-fast", "yt-dlp.exe");
        var executable = File.Exists(fastExecutable) ? fastExecutable : Path.Combine(_toolsPath, "yt-dlp.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Не найден модуль поиска yt-dlp.exe.");
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var value in new[] { "--ignore-config", "--force-ipv4", "--no-plugin-dirs", "--no-cache-dir", "--no-warnings", "--socket-timeout", "8", "--retries", "1", "--extractor-retries", "1", "--encoding", "utf-8" })
            start.ArgumentList.Add(value);
        var deno = Path.Combine(_toolsPath, "deno.exe");
        if (File.Exists(deno)) { start.ArgumentList.Add("--js-runtimes"); start.ArgumentList.Add("deno:" + deno); }
        foreach (var value in arguments) start.ArgumentList.Add(value);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(70));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить модуль поиска.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("Источник отвечает слишком долго. Попробуй ещё раз.");
        }
        var json = await output.ConfigureAwait(false);
        var errorText = await errors.ConfigureAwait(false);
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
        {
            var message = errorText.Split('\n').LastOrDefault(line => line.StartsWith("ERROR:"))?.Trim() ?? "Источник временно недоступен.";
            throw new InvalidOperationException(message.Length > 400 ? message[..400] : message);
        }
        return json;
    }

    public async Task<JsonDocument> ReadPlaylistAsync(string url, CancellationToken token = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0
            || !(uri.Host == "soundcloud.com" && uri.AbsolutePath.Contains("/sets/", StringComparison.Ordinal)
                || uri.Host is "music.yandex.ru" or "music.yandex.com" && (uri.AbsolutePath.Contains("/playlists/", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/playlists/", StringComparison.Ordinal))))
            throw new ArgumentException("Нужна общедоступная ссылка на плейлист SoundCloud или Яндекс Музыки.");
        return JsonDocument.Parse(await RunAsync(["--flat-playlist", "--dump-single-json", "--skip-download", "--playlist-end", "1000", url], token).ConfigureAwait(false));
    }

    private static OnlineTrack ParseTrack(JsonElement root, string provider)
    {
        var url = GetString(root, "webpage_url") ?? GetString(root, "url") ?? "";
        if (provider == "YouTube" && !url.StartsWith("http", StringComparison.Ordinal)) url = "https://www.youtube.com/watch?v=" + GetString(root, "id");
        var duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
        var thumbnail = GetString(root, "thumbnail");
        if (thumbnail == null && root.TryGetProperty("thumbnails", out var thumbnails) && thumbnails.ValueKind == JsonValueKind.Array)
            thumbnail = thumbnails.EnumerateArray().Select(t => GetString(t, "url")).LastOrDefault(t => t != null);
        return new OnlineTrack(GetString(root, "title") ?? "Без названия", GetString(root, "uploader") ?? GetString(root, "channel") ?? provider, provider, url, duration, thumbnail);
    }

    private static string? GetString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool IsSupportedLink(Uri uri) => new[] { "youtube.com", "youtu.be", "soundcloud.com" }.Any(host => uri.Host == host || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
    public void Dispose() => _probe.Dispose();
}
