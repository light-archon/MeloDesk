using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeloDesk.Music;

public sealed record GenrePlaylist(string Key, string Name, string SourcePlaylistId, string SourcePlaylistName,
    IReadOnlyList<OnlineTrack> Tracks, string? Continuation, string Source);

public sealed partial class YouTubeMusicCatalog
{
    private static readonly IReadOnlyDictionary<string, (string Name, string Query)> Genres =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["pop"] = ("Поп", "pop music playlist"),
            ["rock"] = ("Рок", "rock music playlist"),
            ["hiphop"] = ("Хип-хоп", "hip hop music playlist"),
            ["electronic"] = ("Электроника", "electronic music playlist"),
            ["focus"] = ("Для фокуса", "instrumental focus playlist"),
            ["evening"] = ("Тихий вечер", "lofi chillout playlist"),
            ["indie"] = ("Инди", "indie alternative playlist"),
            ["energy"] = ("Больше энергии", "dance workout music playlist")
        };

    public async Task<GenrePlaylist> GetGenrePlaylistAsync(string key, CancellationToken token = default)
    {
        if (!Genres.TryGetValue(key, out var genre)) throw new ArgumentException("Неизвестный жанр или настроение.");
        if (Cached<GenrePlaylist>("genre:" + key) is { } cached) return cached;
        using var search = await RequestAsync("search", new() { ["query"] = genre.Query }, token).ConfigureAwait(false);
        var candidates = Find(search.RootElement, "musicResponsiveListItemRenderer")
            .Concat(Find(search.RootElement, "musicTwoRowItemRenderer"))
            .Where(row => !Text(Path(row, "musicItemRendererDisplayPolicy")).Contains("GREY_OUT", StringComparison.Ordinal))
            .Select(row => (Id: Text(Path(row, "navigationEndpoint", "browseEndpoint", "browseId")),
                Name: Text(Path(row, "title")) is { Length: > 0 } title ? title : Columns(row).Select(Text).FirstOrDefault() ?? ""))
            .Where(p => Regex.IsMatch(p.Id, "^VL[A-Za-z0-9_-]{10,180}$") && p.Name.Length > 0)
            .DistinctBy(p => p.Id).OrderByDescending(p => p.Id.StartsWith("VLRDCLAK", StringComparison.Ordinal)).Take(3).ToArray();
        foreach (var candidate in candidates)
        {
            try
            {
                var page = await ReadPageAsync(key, "genre", [new(candidate.Id)], token).ConfigureAwait(false);
                if (page.Tracks.Count == 0) continue;
                return Remember("genre:" + key, new GenrePlaylist(key, genre.Name, candidate.Id, candidate.Name,
                    page.Tracks, page.Continuation, Source));
            }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException && !token.IsCancellationRequested) { }
        }
        throw new InvalidOperationException("Подборка временно недоступна. Попробуй загрузить её ещё раз.");
    }

    public Task<CatalogPage> GetGenrePageAsync(string key, string continuation, CancellationToken token = default)
    {
        if (!Genres.ContainsKey(key)) throw new ArgumentException("Неизвестный жанр или настроение.");
        return ContinuePageAsync(key, "genre", continuation, token);
    }
}
