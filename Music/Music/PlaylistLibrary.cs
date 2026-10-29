using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MeloDesk.Music;

public sealed record PlaylistOperation(string Id, long Clock, string Kind, string PlaylistId, string? Name = null, OnlineTrack? Track = null, string? TrackUrl = null);
public sealed record PlaylistDocument(int Version, long AccountId, string DeviceId, List<PlaylistOperation> Operations);
public sealed record MusicPlaylist(string Id, string Name, IReadOnlyList<OnlineTrack> Tracks)
{
    public bool IsFavorites => Id == PlaylistMerge.FavoritesId;
    public override string ToString() => $"{Name} · {Tracks.Count}";
}
public sealed record PlaylistCloudCopy(long MessageId, PlaylistDocument Document);
public interface IPlaylistCloud
{
    Task<IReadOnlyList<PlaylistCloudCopy>> LoadAsync(long accountId);
    Task<long> SaveAsync(long accountId, long messageId, PlaylistDocument document);
}

public static class PlaylistMerge
{
    public const string FavoritesId = "00000000-0000-0000-0000-000000000001";
    public static List<PlaylistOperation> Merge(long accountId, IEnumerable<PlaylistDocument> documents) => documents
        .Where(d => d.Version == 1 && d.AccountId == accountId)
        .SelectMany(d => d.Operations).Where(Valid).DistinctBy(o => o.Id).OrderBy(o => o.Clock).ThenBy(o => o.Id, StringComparer.Ordinal).ToList();
    public static IReadOnlyList<MusicPlaylist> Materialize(IEnumerable<PlaylistOperation> operations, bool includeFavorites = false)
    {
        var playlists = new Dictionary<string, (string Name, List<OnlineTrack> Tracks)>();
        if (includeFavorites) playlists[FavoritesId] = ("Любимые", []);
        var deleted = new HashSet<string>();
        foreach (var op in operations.OrderBy(o => o.Clock).ThenBy(o => o.Id, StringComparer.Ordinal))
        {
            if (op.PlaylistId == FavoritesId)
            {
                if (!playlists.ContainsKey(FavoritesId)) playlists[FavoritesId] = ("Любимые", []);
                if (op.Kind is "delete" or "rename" or "create") continue;
            }
            if (op.Kind == "delete") { deleted.Add(op.PlaylistId); playlists.Remove(op.PlaylistId); continue; }
            if (deleted.Contains(op.PlaylistId)) continue;
            if (op.Kind == "create") { playlists.TryAdd(op.PlaylistId, (op.Name ?? "Плейлист", [])); continue; }
            if (!playlists.TryGetValue(op.PlaylistId, out var list)) continue;
            if (op.Kind == "rename") playlists[op.PlaylistId] = (op.Name ?? list.Name, list.Tracks);
            else if (op.Kind == "add" && op.Track != null && !list.Tracks.Any(t => t.Url == op.Track.Url)) list.Tracks.Add(op.Track);
            else if (op.Kind == "remove") list.Tracks.RemoveAll(t => t.Url == op.TrackUrl);
        }
        return playlists.Select(p => new MusicPlaylist(p.Key, p.Value.Name, p.Value.Tracks)).ToArray();
    }
    public static bool Valid(PlaylistOperation op) => Valid(op, false);
    public static bool Valid(PlaylistOperation op, bool allowLocal) => op != null && Guid.TryParse(op.Id, out _) && Guid.TryParse(op.PlaylistId, out _)
        && op.Clock > 0 && op.Kind is "create" or "rename" or "add" or "remove" or "delete"
        && (op.Name == null || op.Name.Length <= 100)
        && (op.Kind != "add" || op.Track != null && (op.Track.IsLocal
            ? allowLocal && Uri.TryCreate(op.Track.Url, UriKind.Absolute, out var local) && local.IsFile
            : IsTrackUrl(op.Track.Url)));
    public static bool IsTrackUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && new[] { "youtube.com", "youtu.be", "soundcloud.com" }.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
}

public sealed class PlaylistLibrary(long accountId, IPlaylistCloud? cloud, string? dataRoot = null, bool localOnly = false)
{
    private sealed record LocalState(string DeviceId, long MessageId, bool Pending, List<PlaylistOperation> Operations);
    private readonly SemaphoreSlim _gate = new(1);
    private readonly string _directory = Path.Combine(dataRoot ?? AppPaths.Root, "accounts", accountId.ToString());
    private string _deviceId = Guid.NewGuid().ToString("N");
    private long _messageId;
    private List<PlaylistOperation> _operations = [];
    private IReadOnlyList<MusicPlaylist>? _snapshot;
    public long AccountId => accountId;
    public bool Pending { get; private set; }
    public IReadOnlyList<MusicPlaylist> Playlists => _snapshot ??= PlaylistMerge.Materialize(_operations, includeFavorites: true);
    public void LoadLocal()
    {
        var path = Path.Combine(_directory, "playlists.dat");
        if (!File.Exists(path)) return;
        var state = JsonSerializer.Deserialize<LocalState>(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
        if (state == null) return;
        _deviceId = state.DeviceId; _messageId = state.MessageId; Pending = state.Pending;
        _operations = state.Operations.Where(op => PlaylistMerge.Valid(op, localOnly)).ToList();
        _snapshot = null;
    }
    public async Task SyncAsync(bool uploadPending)
    {
        await _gate.WaitAsync();
        try
        {
            if (localOnly) { if (Pending) { Pending = false; SaveLocal(); } return; }
            if (cloud == null) throw new InvalidOperationException("Сервер библиотеки не настроен.");
            var remote = await cloud.LoadAsync(accountId);
            _operations = PlaylistMerge.Merge(accountId, remote.Select(c => c.Document).Append(Document()));
            _snapshot = null;
            _messageId = remote.FirstOrDefault(c => c.Document.DeviceId == _deviceId)?.MessageId ?? 0;
            SaveLocal();
            if (uploadPending && Pending)
            {
                _messageId = await cloud.SaveAsync(accountId, _messageId, Document());
                Pending = false;
                SaveLocal();
            }
        }
        finally { _gate.Release(); }
    }
    public async Task ChangeAsync(string kind, string playlistId, string? name = null, OnlineTrack? track = null, string? trackUrl = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (_operations.Count >= (localOnly ? 50000 : 4500)) throw new InvalidOperationException("Достигнут лимит этой версии библиотеки.");
            if (playlistId == PlaylistMerge.FavoritesId && kind is "rename" or "delete" or "create") throw new InvalidOperationException("Раздел «Любимые» нельзя удалить или переименовать.");
            if (kind != "create" && !Playlists.Any(p => p.Id == playlistId)) throw new InvalidOperationException("Плейлист не найден. Открой коллекцию ещё раз.");
            var target = Playlists.FirstOrDefault(p => p.Id == playlistId);
            if (kind == "add" && track != null && target!.Tracks.Any(t => t.Url == track.Url)) return;
            if (kind == "remove" && !target!.Tracks.Any(t => t.Url == trackUrl)) return;
            var clock = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _operations.Count == 0 ? 1 : _operations.Max(o => o.Clock) + 1);
            var op = new PlaylistOperation(Guid.NewGuid().ToString(), clock, kind, playlistId, name?.Trim(), track, trackUrl);
            if (!PlaylistMerge.Valid(op, localOnly)) throw new InvalidOperationException("Локальные файлы можно добавить только в локальную библиотеку.");
            var next = new List<PlaylistOperation>(_operations) { op };
            if (kind == "create" && track != null)
            {
                var addition = new PlaylistOperation(Guid.NewGuid().ToString(), clock + 1, "add", playlistId, Track: track);
                if (!PlaylistMerge.Valid(addition, localOnly)) throw new InvalidOperationException("Этот трек нельзя добавить в плейлист.");
                next.Add(addition);
            }
            var previous = _operations; var wasPending = Pending;
            _operations = next; Pending = !localOnly;
            try { SaveLocal(); }
            catch { _operations = previous; Pending = wasPending; throw; }
            _snapshot = null;
        }
        finally { _gate.Release(); }
        if (!localOnly) await SyncAsync(uploadPending: true);
    }
    public Task SetLikedAsync(OnlineTrack track, bool liked) => ChangeAsync(liked ? "add" : "remove", PlaylistMerge.FavoritesId, track: liked ? track : null, trackUrl: track.Url);
    public async Task<string> ImportAsync(string name, IReadOnlyList<OnlineTrack> tracks, string? playlistId = null)
    {
        if (!localOnly) throw new InvalidOperationException("Импорт доступен для локальной библиотеки.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || tracks.Count is 0 or > 1000) throw new ArgumentException("Укажи название и от 1 до 1000 треков.");
        await _gate.WaitAsync();
        try
        {
            var existing = playlistId == null ? null : Playlists.FirstOrDefault(p => p.Id == playlistId) ?? throw new ArgumentException("Плейлист не найден.");
            var id = playlistId ?? Guid.NewGuid().ToString();
            var next = new List<PlaylistOperation>(_operations);
            var clock = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _operations.Count == 0 ? 1 : _operations.Max(o => o.Clock) + 1);
            if (existing == null) next.Add(new(Guid.NewGuid().ToString(), clock++, "create", id, name.Trim()));
            var urls = new HashSet<string>(existing?.Tracks.Select(t => t.Url) ?? []);
            foreach (var track in tracks)
            {
                if (!urls.Add(track.Url)) continue;
                var op = new PlaylistOperation(Guid.NewGuid().ToString(), clock++, "add", id, Track: track);
                if (!PlaylistMerge.Valid(op, true)) throw new ArgumentException("Некорректный трек в импорте.");
                next.Add(op);
            }
            if (next.Count > 50000) throw new InvalidOperationException("Достигнут лимит библиотеки.");
            var previous = _operations; var pending = Pending;
            _operations = next; Pending = false;
            try { SaveLocal(); } catch { _operations = previous; Pending = pending; throw; }
            _snapshot = null;
            return id;
        }
        finally { _gate.Release(); }
    }
    private PlaylistDocument Document() => new(1, accountId, _deviceId, _operations);
    private void SaveLocal()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "playlists.dat");
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new LocalState(_deviceId, _messageId, Pending, _operations)), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path + ".tmp", bytes); File.Move(path + ".tmp", path, true);
    }
}

