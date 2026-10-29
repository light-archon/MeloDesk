using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MeloDesk.Telegram;

public sealed record TelegramChatPhoto(int FileId, string? LocalPath, byte[]? Minithumbnail, bool HasAnimation);
public sealed record TelegramFileState(int Id, string? Path, bool IsComplete);

public static class TelegramPhotoParser
{
    public static TelegramChatPhoto? ParsePhoto(JsonElement photo)
    {
        if (photo.ValueKind != JsonValueKind.Object || !photo.TryGetProperty("small", out var small)) return null;
        var file = ParseFile(small);
        byte[]? preview = null;
        if (photo.TryGetProperty("minithumbnail", out var miniature) && miniature.ValueKind == JsonValueKind.Object
            && miniature.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
            try { preview = Convert.FromBase64String(data.GetString()!); } catch (FormatException) { }
        return new TelegramChatPhoto(file.Id, file.IsComplete ? file.Path : null, preview,
            photo.TryGetProperty("has_animation", out var animated) && animated.ValueKind == JsonValueKind.True);
    }

    public static TelegramFileState ParseFile(JsonElement file)
    {
        var id = file.GetProperty("id").GetInt32();
        if (!file.TryGetProperty("local", out var local) || local.ValueKind != JsonValueKind.Object) return new(id, null, false);
        var path = local.TryGetProperty("path", out var value) ? value.GetString() : null;
        var complete = local.TryGetProperty("is_downloading_completed", out var completed) && completed.ValueKind == JsonValueKind.True;
        return new(id, path, complete);
    }
}

public sealed class ChatAvatar : INotifyPropertyChanged
{
    private long _version;
    private ImageSource? _image;
    public ImageSource? Image => _image;
    public event PropertyChangedEventHandler? PropertyChanged;
    public long BeginPhoto()
    {
        var version = Interlocked.Increment(ref _version);
        _image = null;
        PropertyChanged?.Invoke(this, new(nameof(Image)));
        return version;
    }
    public bool IsCurrent(long version) => Interlocked.Read(ref _version) == version;
    public void Apply(long version, ImageSource image)
    {
        if (!IsCurrent(version)) return;
        _image = image;
        PropertyChanged?.Invoke(this, new(nameof(Image)));
    }
}

public sealed class ChatAvatarLoader(Func<int, CancellationToken, Task<string?>> download, Action<Action> dispatch)
{
    private readonly ConcurrentDictionary<string, BitmapSource> _images = new(StringComparer.OrdinalIgnoreCase);
    public async Task LoadAsync(ChatAvatar avatar, TelegramChatPhoto? photo, CancellationToken cancellationToken = default)
    {
        var version = avatar.BeginPhoto();
        if (photo == null) return;
        try
        {
            var fullPhoto = !string.IsNullOrWhiteSpace(photo.LocalPath) && File.Exists(photo.LocalPath)
                ? Task.FromResult<string?>(photo.LocalPath)
                : download(photo.FileId, cancellationToken);
            if (photo.Minithumbnail is { Length: > 0 } miniature)
            {
                try
                {
                    var preview = await Task.Run(() => Decode(miniature), cancellationToken).ConfigureAwait(false);
                    dispatch(() => avatar.Apply(version, preview));
                }
                catch (Exception error) when (error is not OperationCanceledException) { }
            }
            var path = await fullPhoto.ConfigureAwait(false);
            if (!avatar.IsCurrent(version) || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            var cacheKey = path + ":" + File.GetLastWriteTimeUtc(path).Ticks;
            if (!_images.TryGetValue(cacheKey, out var bitmap))
            {
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                bitmap = await Task.Run(() => Decode(bytes), cancellationToken).ConfigureAwait(false);
                _images[cacheKey] = bitmap;
                if (_images.Count > 250) _images.Clear();
            }
            var finalImage = bitmap;
            dispatch(() => avatar.Apply(version, finalImage));
        }
        catch (Exception) { }
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 160;
        image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }
}
