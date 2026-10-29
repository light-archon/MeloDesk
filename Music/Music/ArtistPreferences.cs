using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeloDesk.Music;

public sealed class ArtistPreferences(long accountId, string? dataRoot = null)
{
    private readonly string _directory = Path.Combine(dataRoot ?? AppPaths.Root, "accounts", accountId.ToString());
    private IReadOnlyList<OnlineArtist>? _artists;
    public bool OnboardingCompleted
    {
        get
        {
            try
            {
                var path = Path.Combine(_directory, "taste.dat");
                return File.Exists(path) && new FileInfo(path).Length < 4096
                    && ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser).AsSpan().SequenceEqual("taste-v1-complete"u8);
            }
            catch (Exception e) when (e is IOException or CryptographicException) { return false; }
        }
    }
    public void CompleteOnboarding()
    {
        if (Artists.Count < 5) throw new InvalidOperationException("Выбери минимум пять любимых исполнителей.");
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "taste.dat");
        File.WriteAllBytes(path + ".tmp", ProtectedData.Protect("taste-v1-complete"u8.ToArray(), null, DataProtectionScope.CurrentUser));
        File.Move(path + ".tmp", path, true);
    }
    public IReadOnlyList<OnlineArtist> Artists
    {
        get
        {
            if (_artists != null) return _artists;
            var path = Path.Combine(_directory, "artists.dat");
            if (!File.Exists(path)) return _artists = [];
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 1024 * 1024) throw new InvalidOperationException("Файл любимых исполнителей слишком большой.");
            var stored = JsonSerializer.Deserialize<OnlineArtist[]>(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser)) ?? [];
            return _artists = stored.Where(Valid).DistinctBy(a => a.Id).Take(200).ToArray();
        }
    }
    public void SetFavorite(OnlineArtist artist, bool favorite)
    {
        if (!Valid(artist)) throw new ArgumentException("Некорректный исполнитель.");
        var previous = Artists;
        if (favorite && previous.Any(a => a.Id == artist.Id) || !favorite && previous.All(a => a.Id != artist.Id)) return;
        if (favorite && previous.Count >= 200) throw new InvalidOperationException("Можно выбрать до 200 любимых исполнителей.");
        var next = favorite ? previous.Append(artist).ToArray() : previous.Where(a => a.Id != artist.Id).ToArray();
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(next), null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "artists.dat");
        File.WriteAllBytes(path + ".tmp", bytes);
        File.Move(path + ".tmp", path, true);
        _artists = next;
    }
    private static bool Valid(OnlineArtist artist) => artist != null
        && Regex.IsMatch(artist.Id ?? "", "^UC[A-Za-z0-9_-]{8,100}$")
        && !string.IsNullOrWhiteSpace(artist.Name) && artist.Name.Length <= 200
        && (artist.Thumbnail == null || artist.Thumbnail.Length <= 2048 && Uri.TryCreate(artist.Thumbnail, UriKind.Absolute, out var image) && image.Scheme == "https");
}
