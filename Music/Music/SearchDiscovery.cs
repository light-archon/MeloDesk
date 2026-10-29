namespace MeloDesk.Music;

public sealed record SearchDiscovery(IReadOnlyList<OnlineAlbum> Albums, IReadOnlyList<OnlineArtist> Artists,
    IReadOnlyList<string> Messages, string Source, DateTimeOffset RetrievedAt);

public sealed partial class YouTubeMusicCatalog
{
    public async Task<SearchDiscovery> GetSearchDiscoveryAsync(CancellationToken token = default)
    {
        if (Cached<SearchDiscovery>("search-discovery") is { } cached) return cached;
        var releases = ReadNewReleasesAsync(token);
        var artists = DiscoverAsync(token);
        await Task.WhenAll(releases, artists).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var result = await releases.ConfigureAwait(false);
        var discovery = new SearchDiscovery(result.Albums, await artists.ConfigureAwait(false), result.Messages,
            Source, DateTimeOffset.UtcNow);
        // A failed release request must be retried when the user asks to refresh.
        return result.Messages.Count == 0 ? Remember("search-discovery", discovery) : discovery;
    }

    private async Task<(IReadOnlyList<OnlineAlbum> Albums, IReadOnlyList<string> Messages)> ReadNewReleasesAsync(CancellationToken token)
    {
        try
        {
            using var json = await RequestAsync("browse", new() { ["browseId"] = "FEmusic_explore" }, token).ConfigureAwait(false);
            var shelves = Find(json.RootElement, "musicCarouselShelfRenderer").Where(s =>
                Text(Path(s, "header", "musicCarouselShelfBasicHeaderRenderer", "title")) == "New albums & singles");
            var albums = shelves.SelectMany(ParseAlbums).DistinctBy(a => a.Id).Take(12).ToArray();
            return (albums, albums.Length > 0 ? [] : ["Новые релизы пока недоступны в каталоге. Попробуй обновить подборку."]);
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException && !token.IsCancellationRequested)
        {
            return ([], ["Не удалось загрузить новые релизы. Проверь подключение и обнови подборку."]);
        }
    }
}
