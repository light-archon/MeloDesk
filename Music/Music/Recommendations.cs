using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace MeloDesk.Music;

public sealed record RecommendationArtist(OnlineArtist Artist, IReadOnlyList<OnlineArtist> Related, IReadOnlyList<OnlineAlbum> Albums);
public interface IRecommendationCatalog
{
    Task<RecommendationArtist> GetRecommendationArtistAsync(string id, CancellationToken token = default);
}
public sealed record RecommendationSet(string SeedKey, IReadOnlyList<string> SeedNames, IReadOnlyList<OnlineArtist> Artists,
    IReadOnlyList<OnlineAlbum> Albums, IReadOnlyList<string> Messages, DateTimeOffset GeneratedAt, bool Cached = false);

public sealed class RecommendationEngine(IRecommendationCatalog catalog, long accountId, string? dataRoot = null)
{
    private readonly string _path = Path.Combine(dataRoot ?? AppPaths.Root, "accounts", accountId.ToString(), "recommendations.dat");
    public static string SeedKey(IEnumerable<OnlineArtist> artists) => string.Join("|", artists.Select(a => a.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    public async Task<RecommendationSet> GetAsync(IReadOnlyList<OnlineArtist> selected, bool refresh = false, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var seeds = selected.DistinctBy(a => a.Id).OrderBy(a => a.Id, StringComparer.Ordinal).ToArray();
        if (seeds.Length < 5) throw new InvalidOperationException("Выбери минимум пять любимых исполнителей.");
        var key = SeedKey(seeds);
        var previous = ReadCache(key);
        if (!refresh && previous is not null && DateTimeOffset.UtcNow - previous.GeneratedAt < (previous.Messages.Count > 0 ? TimeSpan.FromMinutes(2) : TimeSpan.FromHours(6))) return previous with { Cached = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var failed = 0;
        async Task<RecommendationArtist?> Fetch(OnlineArtist artist)
        {
            try { return await catalog.GetRecommendationArtistAsync(artist.Id, deadline.Token).ConfigureAwait(false); }
            catch (Exception e) when (!token.IsCancellationRequested && e is InvalidOperationException or TimeoutException or OperationCanceledException or HttpRequestException)
            { Interlocked.Increment(ref failed); return null; }
        }
        // Round-robin through all selected artists across days rather than permanently ignoring picks after the limit.
        var offset = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86400 % seeds.Length);
        var active = seeds.Skip(offset).Concat(seeds.Take(offset)).Take(12).ToArray();
        var profiles = (await Task.WhenAll(active.Select(Fetch)).ConfigureAwait(false)).OfType<RecommendationArtist>().ToArray();
        token.ThrowIfCancellationRequested();
        var chosen = seeds.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var candidates = profiles.SelectMany(p => p.Related.Where(a => !chosen.Contains(a.Id)).DistinctBy(a => a.Id)
                .Select((a, rank) => new { Artist = a, Seed = p.Artist.Id, Weight = 1.0 / (rank + 2) }))
            .GroupBy(x => x.Artist.Id).Select(g => new { Artist = g.First().Artist, Score = g.Select(x => x.Seed).Distinct().Count() * 2 + g.Sum(x => x.Weight) })
            .OrderByDescending(x => x.Score).ThenBy(x => x.Artist.Id, StringComparer.Ordinal).Take(12).Select(x => x.Artist).ToArray();
        var relatedProfiles = (await Task.WhenAll(candidates.Take(4).Select(Fetch)).ConfigureAwait(false)).OfType<RecommendationArtist>().ToArray();
        token.ThrowIfCancellationRequested();
        var albums = Diversify(profiles.Concat(relatedProfiles).ToArray());
        if (profiles.Length == 0 && previous is not null)
            return previous with { Cached = true, Messages = ["Каталог временно недоступен. Показываем последнюю сохранённую подборку."] };
        var messages = new List<string>();
        if (failed > 0) messages.Add("Часть каталога не ответила. Подборку можно обновить позже.");
        if (candidates.Length == 0) messages.Add("Похожие исполнители пока недоступны для выбранных артистов.");
        if (albums.Count == 0) messages.Add("Не удалось получить альбомы. Попробуй обновить подборку.");
        var result = new RecommendationSet(key, seeds.Select(a => a.Name).ToArray(), candidates, albums, messages, DateTimeOffset.UtcNow);
        if (albums.Count > 0 || candidates.Length > 0)
        {
            try
            {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(result), null, DataProtectionScope.CurrentUser);
            if (bytes.Length <= 2 * 1024 * 1024)
            {
                var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllBytes(temporary, bytes); token.ThrowIfCancellationRequested(); File.Move(temporary, _path, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
            { result = result with { Messages = result.Messages.Append("Подборка готова, но её не удалось сохранить на этом компьютере.").ToArray() }; }
        }
        return result;
    }

    private static IReadOnlyList<OnlineAlbum> Diversify(RecommendationArtist[] profiles)
    {
        var albums = new List<OnlineAlbum>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        var owners = new HashSet<string>(StringComparer.Ordinal);
        var groups = profiles.Where(p => owners.Add(p.Artist.Id)).Select(p => p.Albums
            .Where(a => a.Type == "Album").OrderByDescending(a => int.TryParse(a.Year, out var y) ? y : 0).ToArray()).ToArray();
        for (var round = 0; round < 2 && albums.Count < 16; round++)
            foreach (var group in groups)
                if (round < group.Length && ids.Add(group[round].Id)) { albums.Add(group[round]); if (albums.Count == 16) break; }
        return albums;
    }
    private RecommendationSet? ReadCache(string key)
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 2 * 1024 * 1024) return null;
            var result = JsonSerializer.Deserialize<RecommendationSet>(ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser));
            return result?.SeedKey == key ? result : null;
        }
        catch (Exception e) when (e is IOException or CryptographicException or JsonException) { return null; }
    }
}

public sealed partial class YouTubeMusicCatalog : IRecommendationCatalog
{
    public async Task<IReadOnlyList<OnlineArtist>> GetArtistChoicesAsync(string query, CancellationToken token = default)
    {
        if (!string.IsNullOrWhiteSpace(query)) return await SearchArtistsAsync(query, token).ConfigureAwait(false);
        if (Cached<OnlineArtist[]>("taste-choices") is { } cached) return cached;
        var groups = await Task.WhenAll(new[] { "Noize MC", "Кино", "The Weeknd", "Billie Eilish", "Linkin Park", "Daft Punk" }.Select(async name =>
        {
            try { return (await SearchArtistsAsync(name, token).ConfigureAwait(false)).Take(1).ToArray(); }
            catch (Exception e) when (!token.IsCancellationRequested && e is InvalidOperationException or TimeoutException) { return []; }
        })).ConfigureAwait(false);
        var result = groups.SelectMany(a => a).DistinctBy(a => a.Id).ToArray();
        return result.Length > 0 ? Remember("taste-choices", result) : result;
    }

    public async Task<RecommendationArtist> GetRecommendationArtistAsync(string id, CancellationToken token = default)
    {
        if (!ValidArtistId(id)) throw new ArgumentException("Некорректный исполнитель.");
        if (Cached<RecommendationArtist>("taste-artist:" + id) is { } cached) return cached;
        using var document = await RequestAsync("browse", new() { ["browseId"] = id }, token).ConfigureAwait(false);
        var root = document.RootElement;
        var header = Find(root, "musicImmersiveHeaderRenderer").Concat(Find(root, "musicVisualHeaderRenderer")).FirstOrDefault();
        var name = Text(Path(header, "title"));
        if (name.Length == 0) throw new InvalidOperationException("Страница исполнителя временно недоступна.");
        var shelves = Find(root, "musicCarouselShelfRenderer").ToArray();
        var related = shelves.Where(s => Text(Path(s, "header", "musicCarouselShelfBasicHeaderRenderer", "title")) is "Fans might also like" or "Similar artists" or "Related artists")
            .SelectMany(s => Find(s, "musicTwoRowItemRenderer")).Select(row => new OnlineArtist(
                Text(Path(row, "navigationEndpoint", "browseEndpoint", "browseId")), Text(Path(row, "title")),
                Thumbnail(row.TryGetProperty("thumbnailRenderer", out var image) ? JsonSerializer.SerializeToElement(new { thumbnail = image }) : row)))
            .Where(a => ValidArtistId(a.Id) && a.Name.Length > 0 && a.Id != id).DistinctBy(a => a.Id).Take(16).ToArray();
        var albums = shelves.Where(s => Text(Path(s, "header", "musicCarouselShelfBasicHeaderRenderer", "title")) == "Albums")
            .SelectMany(ParseAlbums).Select(a => string.IsNullOrWhiteSpace(a.Artist) ? a with { Artist = name } : a).DistinctBy(a => a.Id).Take(8).ToArray();
        return Remember("taste-artist:" + id, new RecommendationArtist(new(id, name, Thumbnail(header)), related, albums));
    }
}
