using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeloDesk.Music;

using var host = new MusicHost(args);
await host.RunAsync();

sealed class MusicHost : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private readonly object _outputLock = new();
    private readonly StreamReader _input = new(Console.OpenStandardInput(), new UTF8Encoding(false));
    private readonly StreamWriter _output = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
    private readonly object _searchLock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly OnlineMusicSearch _search = new();
    private readonly YouTubeMusicCatalog _catalog = new();
    private readonly PlayerPreferences _preferences;
    private readonly PlaylistTransfer _transfer;
    private readonly LyricsService _lyrics = new();
    private readonly PlaybackQueue _playQueue = new();
    private readonly object _importLock = new();
    private CancellationTokenSource? _importCancellation;
    private readonly StreamResolutionCache _cache;
    private AudioPlayback _audio = null!;
    private TrackPlaybackController _playback = null!;
    private readonly Task _initialization;
    private readonly StreamPreparationQueue _prepare = new();
    private readonly SemaphoreSlim _libraryGate = new(1);
    private readonly long _accountId;
    private readonly List<Task> _requests = [];
    private CancellationTokenSource? _searchCancellation;
    private OnlineTrack[] _queue = [];
    private PlaylistLibrary? _library;
    private ArtistPreferences? _artistPreferences;
    private RecommendationEngine? _recommendations;
    private readonly object _recommendationSync = new();
    private CancellationTokenSource? _recommendationCancel;
    private CancellationTokenSource? _artistChoiceCancel;
    private bool _disposed;

    public MusicHost(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("--account=")) long.TryParse(arg[10..], out _accountId);
        }
        _preferences = new PlayerPreferences(_accountId);
        if (_accountId > 0) _preferences.Load();
        _transfer = new(_catalog, _search);
        _cache = new(_search.ResolveAudioAsync);
        _initialization = Task.Run(() =>
        {
        _audio = new() { Volume = 70 };
        ApplyOptions(_preferences.Options);
        _playback = new(_cache, _audio);
        _playback.Changed += state =>
        {
            if (_playback.Current?.RequestId != state.RequestId) return;
            Write(new { Event = "playback", Snapshot = state });
            if (state.State == TrackPlaybackState.Playing) PrepareNext(state.Track);
            else if (state.State == TrackPlaybackState.Ended) _ = NextAsync(1, state.RequestId);
        };
        });
    }
    public async Task RunAsync()
    {
        Write(new { Event = "ready", Protocol = 1, Version = "0.19.0" });
        while (await _input.ReadLineAsync(_shutdown.Token) is { } line)
        {
            if (line.Length > 16 * 1024 * 1024) { Write(new { Event = "error", Error = "Запрос слишком большой." }); continue; }
            _requests.RemoveAll(t => t.IsCompleted);
            if (_requests.Count >= 32) { Write(new { Event = "error", Error = "Слишком много одновременных запросов." }); continue; }
            _requests.Add(HandleAsync(line));
        }
        _shutdown.Cancel();
        try { await Task.WhenAll(_requests).WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
    }
    private async Task HandleAsync(string line)
    {
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var request = document.RootElement;
            id = Get(request, "id"); var command = Get(request, "command");
            if (command is not ("ping" or "search" or "searchDiscovery" or "genre" or "genrePage" or "discover" or "artist" or "artistPage" or "album" or "albumPage" or "trackStats" or "queue" or "library" or "playlistChange" or "like" or "favoriteArtist" or "completeTaste" or "recommendations" or "artistChoices" or "localAdd" or "importPreview" or "importResolve" or "importChoice" or "importCancel" or "importCommit" or "lyrics"))
                await _initialization.WaitAsync(_shutdown.Token);
            switch (command)
            {
                case "ping": Reply(id, new { Protocol = 1 }); break;
                case "search":
                    var query = Get(request, "query");
                    if (query.Length is 0 or > 500) throw new ArgumentException("Введи название трека или ссылку.");
                    var source = Get(request, "source");
                    if (source.Length == 0) source = "YouTube Music";
                    if (source is not ("YouTube Music" or "SoundCloud")) throw new ArgumentException("Неизвестный источник поиска.");
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    lock (_searchLock) { _searchCancellation?.Cancel(); _searchCancellation = cancellation; }
                    try
                    {
                        var result = source == "SoundCloud"
                            ? await _search.SearchAsync(query, "SoundCloud", cancellation.Token)
                            : await _catalog.SearchAsync(query, cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        foreach (var track in result.Tracks.Take(2)) Prepare(track);
                        Reply(id, new { result.Tracks, result.Artists, result.Messages, Query = query, Source = source });
                    }
                    finally { lock (_searchLock) { if (ReferenceEquals(_searchCancellation, cancellation)) _searchCancellation = null; cancellation.Dispose(); } }
                    break;
                case "discover": Reply(id, new { Artists = await _catalog.DiscoverAsync(_shutdown.Token), Source = "YouTube Music" }); break;
                case "artistChoices":
                    var choiceQuery = Get(request, "query");
                    CancellationTokenSource choiceCancel;
                    lock (_recommendationSync)
                    {
                        _artistChoiceCancel?.Cancel();
                        _artistChoiceCancel = choiceCancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    }
                    try { Reply(id, new { Query = choiceQuery, Artists = await _catalog.GetArtistChoicesAsync(choiceQuery, choiceCancel.Token) }); }
                    finally
                    {
                        lock (_recommendationSync) { if (_artistChoiceCancel == choiceCancel) _artistChoiceCancel = null; }
                        choiceCancel.Dispose();
                    }
                    break;
                case "recommendations":
                    OnlineArtist[] tasteSeeds;
                    await _libraryGate.WaitAsync(_shutdown.Token);
                    try { tasteSeeds = (_artistPreferences ??= new ArtistPreferences(_accountId)).Artists.ToArray(); }
                    finally { _libraryGate.Release(); }
                    CancellationTokenSource recommendationCancel;
                    lock (_recommendationSync)
                    {
                        _recommendationCancel?.Cancel();
                        _recommendationCancel = recommendationCancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                        _recommendations ??= new RecommendationEngine(_catalog, _accountId);
                    }
                    try { Reply(id, new { RequestKey = Get(request, "requestKey"), Feed = await _recommendations.GetAsync(tasteSeeds,
                        request.TryGetProperty("refresh", out var refreshTaste) && refreshTaste.ValueKind == JsonValueKind.True,
                        recommendationCancel.Token) }); }
                    finally
                    {
                        lock (_recommendationSync) { if (_recommendationCancel == recommendationCancel) _recommendationCancel = null; }
                        recommendationCancel.Dispose();
                    }
                    break;
                case "searchDiscovery": Reply(id, await _catalog.GetSearchDiscoveryAsync(_shutdown.Token)); break;
                case "genre":
                    var genre = await _catalog.GetGenrePlaylistAsync(Get(request, "key"), _shutdown.Token);
                    foreach (var track in genre.Tracks.Take(2)) Prepare(track);
                    Reply(id, genre); break;
                case "genrePage": Reply(id, new { Page = await _catalog.GetGenrePageAsync(Get(request, "key"), Get(request, "continuation"), _shutdown.Token), Previous = Get(request, "continuation") }); break;
                case "artist":
                    var artist = await _catalog.GetArtistAsync(Get(request, "artistId"), _shutdown.Token);
                    foreach (var track in artist.Tracks.Take(2)) Prepare(track);
                    Reply(id, artist); break;
                case "artistPage": Reply(id, new { Page = await _catalog.GetArtistPageAsync(Get(request, "artistId"), Get(request, "kind"), Get(request, "continuation"), _shutdown.Token), Kind = Get(request, "kind"), Previous = Get(request, "continuation") }); break;
                case "album":
                    var album = await _catalog.GetAlbumAsync(Get(request, "albumId"), _shutdown.Token);
                    foreach (var track in album.Tracks.Take(2)) Prepare(track);
                    Reply(id, album); break;
                case "albumPage": Reply(id, new { Page = await _catalog.GetAlbumPageAsync(Get(request, "albumId"), Get(request, "continuation"), _shutdown.Token), Previous = Get(request, "continuation") }); break;
                case "trackStats": Reply(id, new { VideoId = Get(request, "videoId"), Plays = await _catalog.GetTrackStatsAsync(Get(request, "videoId"), _shutdown.Token), Source = "YouTube Music" }); break;
                case "select":
                    var selected = Track(request.GetProperty("track"));
                    var current = _playback.Current;
                    if (request.TryGetProperty("toggle", out var toggle) && toggle.ValueKind == JsonValueKind.True && current?.Track.Url == selected.Url)
                    {
                        if (current.State == TrackPlaybackState.Playing) _playback.SetPaused(true);
                        else if (current.State == TrackPlaybackState.Paused) _playback.SetPaused(false);
                        else if (current.State == TrackPlaybackState.Loading) _playback.Stop();
                        else await _playback.SelectAsync(selected);
                    }
                    else await _playback.SelectAsync(selected);
                    Reply(id, _playback.Current); break;
                case "queue":
                    var queue = request.GetProperty("tracks").EnumerateArray().Select(Track).Take(10000).ToArray();
                    Volatile.Write(ref _queue, queue); _playQueue.Install(queue, _preferences.Options.Shuffle, Get(request, "startUrl", _playback?.Current?.Track.Url ?? ""));
                    Reply(id, true); break;
                case "queueState":
                    var order = _playQueue.Tracks;
                    var currentIndex = order.ToList().FindIndex(t => t.Url == _playback.Current?.Track.Url);
                    var offset = request.TryGetProperty("offset", out var offsetJson) ? Math.Max(0, offsetJson.GetInt32()) : Math.Max(0, currentIndex / 100 * 100);
                    Reply(id, new { Tracks = order.Skip(offset).Take(100).ToArray(), Total = order.Count, Offset = offset, CurrentIndex = currentIndex }); break;
                case "controls":
                    var previousShuffle = _preferences.Options.Shuffle;
                    var options = _preferences.Update(o => o with {
                        Shuffle = request.TryGetProperty("shuffle", out var shuffle) ? shuffle.GetBoolean() : o.Shuffle,
                        Repeat = request.TryGetProperty("repeat", out var repeat) ? repeat.GetString() ?? "off" : o.Repeat,
                        EqEnabled = request.TryGetProperty("eqEnabled", out var enabled) ? enabled.GetBoolean() : o.EqEnabled,
                        Preamp = request.TryGetProperty("preamp", out var preamp) ? preamp.GetSingle() : o.Preamp,
                        Bands = request.TryGetProperty("bands", out var bands) ? bands.EnumerateArray().Select(b => b.GetSingle()).ToArray() : o.Bands,
                        Muted = request.TryGetProperty("muted", out var muted) ? muted.GetBoolean() : o.Muted
                    });
                    ApplyOptions(options);
                    if (previousShuffle != options.Shuffle) _playQueue.Reorder(options.Shuffle, _playback.Current?.Track.Url);
                    if (_playback.Current is { State: TrackPlaybackState.Playing } playing) PrepareNext(playing.Track);
                    Reply(id, State()); break;
                case "dislike":
                    var dislikedTrack = Track(request.GetProperty("track")); var disliked = request.GetProperty("disliked").GetBoolean();
                    _preferences.SetDisliked(dislikedTrack.Url, disliked);
                    if (disliked && _playback.Current?.Track.Url == dislikedTrack.Url) { if (!await NextAsync(1)) _playback.Stop(); }
                    Reply(id, State()); break;
                case "lyrics": Reply(id, await _lyrics.GetAsync(Track(request.GetProperty("track")), _shutdown.Token)); break;
                case "localAdd":
                    var local = new LocalAudioLibrary(_accountId);
                    var localTracks = await local.AddAsync(request.GetProperty("paths").EnumerateArray().Select(p => p.GetString() ?? ""), _shutdown.Token);
                    await _libraryGate.WaitAsync(_shutdown.Token);
                    try {
                        var localLibrary = await GetLibraryAsync(); var target = Get(request, "playlistId");
                        var localId = await localLibrary.ImportAsync(Get(request, "name", "Мои песни"), localTracks, target.Length > 0 ? target : null);
                        Reply(id, new { localLibrary.Playlists, localLibrary.Pending, PlaylistId = localId, Added = localTracks.Count });
                    } finally { _libraryGate.Release(); }
                    break;
                case "importPreview":
                    lock (_importLock) _importCancellation?.Cancel();
                    Reply(id, await _transfer.PreviewAsync(Get(request, "input"), request.TryGetProperty("file", out var isFile) && isFile.GetBoolean(), _shutdown.Token)); break;
                case "importResolve":
                    var importToken = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    lock (_importLock) { _importCancellation?.Cancel(); _importCancellation = importToken; }
                    try { var previewId = Get(request, "previewId"); Reply(id, await _transfer.ResolveAsync(previewId, (done, total) => Write(new { Event = "importProgress", PreviewId = previewId, Done = done, Total = total }), importToken.Token)); }
                    finally { lock (_importLock) { if (ReferenceEquals(_importCancellation, importToken)) _importCancellation = null; importToken.Dispose(); } }
                    break;
                case "importCancel": lock (_importLock) _importCancellation?.Cancel(); Reply(id, true); break;
                case "importChoice": Reply(id, _transfer.Choose(Get(request, "previewId"), request.GetProperty("index").GetInt32(), Get(request, "url"))); break;
                case "importCommit":
                    await _libraryGate.WaitAsync(_shutdown.Token);
                    try {
                        var destination = await GetLibraryAsync(); var imported = await _transfer.CommitAsync(Get(request, "previewId"), Get(request, "name"), destination);
                        Reply(id, new { destination.Playlists, destination.Pending, PlaylistId = imported.Id, imported.Added, imported.Unresolved });
                    } finally { _libraryGate.Release(); }
                    break;
                case "prepare": Prepare(Track(request.GetProperty("track"))); Reply(id, true); break;
                case "pause":
                    var pause = request.GetProperty("paused").GetBoolean();
                    if (!pause && _playback.Current is { State: TrackPlaybackState.Error or TrackPlaybackState.Ended or TrackPlaybackState.Stopped } retry)
                        await _playback.SelectAsync(retry.Track);
                    else _playback.SetPaused(pause);
                    Reply(id, true); break;
                case "stop": _playback.Stop(); Reply(id, true); break;
                case "next": await NextAsync(1); Reply(id, _playback.Current); break;
                case "previous": if (_audio.IsSeekable && _audio.Time > 3000) _audio.Position = 0; else await NextAsync(-1); Reply(id, _playback.Current); break;
                case "volume": ApplyOptions(_preferences.Update(o => o with { Volume = Math.Clamp(request.GetProperty("value").GetInt32(), 0, 100), Muted = false })); Reply(id, _audio.Volume); break;
                case "seek":
                    var position = request.GetProperty("position").GetDouble();
                    if (!double.IsFinite(position)) throw new ArgumentException("Некорректная позиция.");
                    if (_audio.IsSeekable) _audio.Position = (float)Math.Clamp(position, 0, 1); Reply(id, true); break;
                case "state": Reply(id, State()); break;
                case "library":
                case "playlistChange":
                case "like":
                case "completeTaste":
                case "favoriteArtist":
                    await _libraryGate.WaitAsync(_shutdown.Token);
                    try
                    {
                        var library = await GetLibraryAsync();
                        var preferences = _artistPreferences ??= new ArtistPreferences(_accountId);
                        if (command == "library") await library.SyncAsync(true);
                        else if (command == "completeTaste")
                        {
                            preferences.CompleteOnboarding();
                            Reply(id, new { OnboardingCompleted = true, FavoriteArtists = preferences.Artists });
                        }
                        else if (command == "favoriteArtist")
                        {
                            var favorite = JsonSerializer.Deserialize<OnlineArtist>(request.GetProperty("artist"), Json) ?? throw new ArgumentException("Исполнитель не указан.");
                            preferences.SetFavorite(favorite, request.GetProperty("favorite").GetBoolean());
                            Reply(id, new { FavoriteArtists = preferences.Artists, preferences.OnboardingCompleted });
                        }
                        else if (command == "like")
                        {
                            var likedTrack = Track(request.GetProperty("track")); var liked = request.GetProperty("liked").GetBoolean();
                            await library.SetLikedAsync(likedTrack, liked);
                            if (liked) _preferences.SetDisliked(likedTrack.Url, false);
                            Reply(id, new { Change = new { Kind = liked ? "add" : "remove", PlaylistId = PlaylistMerge.FavoritesId, Track = likedTrack, TrackUrl = likedTrack.Url }, library.Pending });
                        }
                        else
                        {
                            await library.ChangeAsync(Get(request, "kind"), Get(request, "playlistId"),
                                Get(request, "name"), request.TryGetProperty("track", out var trackJson) && trackJson.ValueKind == JsonValueKind.Object ? Track(trackJson) : null, Get(request, "trackUrl"));
                            Reply(id, new { Change = new { Kind = Get(request, "kind"), PlaylistId = Get(request, "playlistId"), Name = Get(request, "name"), Track = request.TryGetProperty("track", out var changedTrack) && changedTrack.ValueKind == JsonValueKind.Object ? Track(changedTrack) : null, TrackUrl = Get(request, "trackUrl") }, library.Pending });
                        }
                        if (command == "library") Reply(id, new { Playlists = library.Playlists, library.Pending, FavoriteArtists = preferences.Artists, preferences.OnboardingCompleted });
                    }
                    finally { _libraryGate.Release(); }
                    break;
                default: throw new ArgumentException("Неизвестная команда музыкального сервиса.");
            }
        }
        catch (OperationCanceledException) { Write(new { Id = id, Ok = false, Cancelled = true }); }
        catch (Exception error) { Write(new { Id = id, Ok = false, Error = error.Message }); }
    }
    private void Prepare(OnlineTrack track) => _prepare.Prepare(track, async (item, token) =>
    {
        var stream = await _cache.ResolveAudioAsync(item, token);
        await _initialization.WaitAsync(token); await _audio.PrepareAsync(stream); return stream;
    });
    private void PrepareNext(OnlineTrack track)
    {
        var next = _playQueue.Next(track.Url, 1, _preferences.Options.Repeat, true, _preferences.IsDisliked);
        if (next != null && next.Url != track.Url) Prepare(next);
    }
    private async Task<bool> NextAsync(int direction, long? expectedRequestId = null)
    {
        var current = _playback.Current;
        if (expectedRequestId is { } requestId && current?.RequestId != requestId) return false;
        var next = _playQueue.Next(current?.Track.Url, direction, _preferences.Options.Repeat, expectedRequestId != null, _preferences.IsDisliked);
        if (next == null) return false;
        if (expectedRequestId is { } expected) await _playback.SelectIfCurrentAsync(next, expected);
        else await _playback.SelectAsync(next);
        return true;
    }
    private object State() => new { Playback = _playback.Current, Time = _audio.Time, Length = _audio.Length,
        Volume = _preferences.Options.Volume, Muted = _preferences.Options.Muted || _audio.IsMuted,
        Options = _preferences.Options, Disliked = _preferences.IsDisliked(_playback.Current?.Track.Url ?? "") };
    private void ApplyOptions(PlayerOptions options) { _audio.Volume = options.Muted ? 0 : options.Volume; _audio.SetEqualizer(options); }
    private Task<PlaylistLibrary> GetLibraryAsync()
    {
        if (_accountId <= 0) throw new InvalidOperationException("Аккаунт Telegram ещё не передан музыкальному сервису.");
        if (_library == null)
        {
            _library = new PlaylistLibrary(_accountId, null, localOnly: true);
            _library.LoadLocal();
        }
        return Task.FromResult(_library);
    }
    private static OnlineTrack Track(JsonElement value)
    {
        var track = value.Deserialize<OnlineTrack>(Json) ?? throw new ArgumentException("Нет данных трека.");
        if (track.Title is null || track.Artist is null || track.Title.Length > 1000 || track.Artist.Length > 1000 || !double.IsFinite(track.Duration) || track.Duration < 0) throw new ArgumentException("Некорректные данные трека.");
        if (!track.IsLocal && !PlaylistMerge.IsTrackUrl(track.Url)) throw new ArgumentException("Поддерживаются ссылки YouTube и SoundCloud.");
        if (track.IsLocal && (!Uri.TryCreate(track.Url, UriKind.Absolute, out var uri) || !uri.IsFile || !File.Exists(uri.LocalPath))) throw new ArgumentException("Локальный файл не найден.");
        return track;
    }
    private static string Get(JsonElement root, string key, string fallback = "") => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private void Reply(string? id, object? result) => Write(new { Id = id, Ok = true, Result = result });
    private void Write(object value) { lock (_outputLock) _output.WriteLine(JsonSerializer.Serialize(value, Json)); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _shutdown.Cancel(); lock (_searchLock) _searchCancellation?.Cancel();
        _prepare.Dispose();
        lock (_importLock) _importCancellation?.Cancel();
        try { _initialization.GetAwaiter().GetResult(); } catch (Exception) { }
        if (_playback != null) _playback.Dispose(); else _audio?.Dispose();
        _cache.Dispose(); _search.Dispose(); _catalog.Dispose(); _transfer.Dispose(); _lyrics.Dispose(); _shutdown.Dispose();
    }
}
