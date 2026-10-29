using MeloDesk.Music;

internal static class ArtistPreferenceChecks
{
    public static Task Persistence()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "artists-" + Guid.NewGuid().ToString("N"));
        var artist = new OnlineArtist("UCmMUZbaYdNH0bEd1PAlAqsA", "Oasis", "https://example.test/cover.jpg", null);
        var preferences = new ArtistPreferences(42, root);
        if (preferences.Artists.Count != 0) throw new Exception("New account is not empty.");
        preferences.SetFavorite(artist, true);
        preferences.SetFavorite(artist, true);
        var reopened = new ArtistPreferences(42, root);
        if (reopened.Artists.Count != 1 || reopened.Artists[0].Id != artist.Id) throw new Exception("Favorite artist was lost or duplicated.");
        if (new ArtistPreferences(43, root).Artists.Count != 0) throw new Exception("Artist favorites crossed accounts.");
        try { reopened.SetFavorite(artist with { Id = "../../other-account" }, true); throw new Exception("Invalid artist accepted."); }
        catch (ArgumentException) { }
        reopened.SetFavorite(artist, false);
        if (new ArtistPreferences(42, root).Artists.Count != 0) throw new Exception("Removing favorite artist did not persist.");
        if (Directory.GetParent(Path.GetFullPath(root))!.FullName != Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory))) throw new Exception("Fixture cleanup escaped its root.");
        Directory.Delete(root, true);
        return Task.CompletedTask;
    }
}
