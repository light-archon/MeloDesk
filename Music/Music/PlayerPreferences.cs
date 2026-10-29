using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MeloDesk.Music;

public sealed record PlayerOptions(bool Shuffle = false, string Repeat = "off", bool EqEnabled = false,
    float Preamp = 0, float[]? Bands = null, int Volume = 70, bool Muted = false);

public sealed class PlayerPreferences(long accountId, string? dataRoot = null)
{
    private sealed record Stored(PlayerOptions Options, string[] Disliked);
    private readonly object _sync = new();
    private readonly string _path = Path.Combine(dataRoot ?? AppPaths.Root, "accounts", accountId.ToString(), "player.dat");
    private PlayerOptions _options = new(Bands: new float[10]);
    private HashSet<string> _disliked = [];
    public PlayerOptions Options { get { lock (_sync) return _options with { Bands = _options.Bands!.ToArray() }; } }
    public bool IsDisliked(string url) { lock (_sync) return _disliked.Contains(url); }
    public void Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_path)) return;
            var bytes = File.ReadAllBytes(_path);
            if (bytes.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Файл настроек плеера слишком большой.");
            var stored = JsonSerializer.Deserialize<Stored>(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
            if (stored == null) return;
            Validate(stored.Options);
            _options = stored.Options with { Bands = stored.Options.Bands ?? new float[10] };
            _disliked = stored.Disliked.Where(s => s.Length < 4096).Take(3000).ToHashSet();
        }
    }
    public void Set(PlayerOptions options)
    {
        Validate(options);
        lock (_sync) { Save(options, _disliked); _options = options with { Bands = options.Bands?.ToArray() ?? new float[10] }; }
    }
    public PlayerOptions Update(Func<PlayerOptions, PlayerOptions> change)
    {
        lock (_sync) { Set(change(Options)); return Options; }
    }
    public void SetDisliked(string url, bool disliked)
    {
        if (url.Length is 0 or > 4096) throw new ArgumentException("Некорректный трек.");
        lock (_sync)
        {
            var next = new HashSet<string>(_disliked);
            if (disliked) next.Add(url); else next.Remove(url);
            if (next.Count > 3000) throw new InvalidOperationException("Достигнут лимит скрытых треков.");
            Save(_options, next); _disliked = next;
        }
    }
    private static void Validate(PlayerOptions options)
    {
        if (options.Repeat is not ("off" or "all" or "one") || options.Volume is < 0 or > 100
            || !float.IsFinite(options.Preamp) || options.Preamp is < -20 or > 20
            || options.Bands != null && (options.Bands.Length != 10 || options.Bands.Any(b => !float.IsFinite(b) || b is < -20 or > 20))) throw new ArgumentException("Некорректные настройки плеера.");
    }
    private void Save(PlayerOptions options, IEnumerable<string> disliked)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new Stored(options, disliked.ToArray())), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path + ".tmp", bytes); File.Move(_path + ".tmp", _path, true);
    }
}

public sealed class PlaybackQueue
{
    private readonly object _sync = new();
    private OnlineTrack[] _original = [];
    private OnlineTrack[] _order = [];
    public IReadOnlyList<OnlineTrack> Tracks { get { lock (_sync) return _order.ToArray(); } }
    public void Install(IEnumerable<OnlineTrack> tracks, bool shuffle, string? anchor = null, Random? random = null)
    {
        lock (_sync) { _original = tracks.DistinctBy(t => t.Url).Take(10000).ToArray(); Reorder(shuffle, anchor, random); }
    }
    public void Reorder(bool shuffle, string? anchor, Random? random = null)
    {
        lock (_sync)
        {
            _order = _original.ToArray();
            if (!shuffle) return;
            random ??= Random.Shared;
            for (var i = _order.Length - 1; i > 0; i--) { var j = random.Next(i + 1); (_order[i], _order[j]) = (_order[j], _order[i]); }
            var current = Array.FindIndex(_order, t => t.Url == anchor);
            if (current > 0) _order = _order.Skip(current).Concat(_order.Take(current)).ToArray();
        }
    }
    public OnlineTrack? Next(string? currentUrl, int direction, string repeat, bool automatic, Func<string, bool>? excluded = null)
    {
        lock (_sync)
        {
            if (_order.Length == 0) return null;
            var current = Array.FindIndex(_order, t => t.Url == currentUrl);
            if (automatic && repeat == "one" && current >= 0 && excluded?.Invoke(currentUrl!) != true) return _order[current];
            if (current < 0) current = direction > 0 ? -1 : _order.Length;
            for (var step = 1; step <= _order.Length; step++)
            {
                var index = current + direction * step;
                if (index < 0 || index >= _order.Length) { if (repeat != "all") return null; index = (index % _order.Length + _order.Length) % _order.Length; }
                if (excluded?.Invoke(_order[index].Url) != true) return _order[index];
            }
            return null;
        }
    }
}
