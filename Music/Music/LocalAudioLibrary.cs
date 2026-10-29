using System.IO;
using System.Security.Cryptography;

namespace MeloDesk.Music;

public sealed class LocalAudioLibrary(long accountId, string? dataRoot = null)
{
    private readonly string _root = Path.Combine(dataRoot ?? AppPaths.Root, "accounts", accountId.ToString(), "audio");
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wav", ".aac", ".wma" };
    public async Task<IReadOnlyList<OnlineTrack>> AddAsync(IEnumerable<string> paths, CancellationToken token = default)
    {
        var files = paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(1001).ToArray();
        if (files.Length is 0 or > 1000) throw new ArgumentException("Выбери от 1 до 1000 аудиофайлов.");
        var tracks = new List<OnlineTrack>();
        Directory.CreateDirectory(_root);
        foreach (var source in files)
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(Path.GetFullPath(source));
            if (!info.Exists || !Extensions.Contains(info.Extension) || info.Length > 1024L * 1024 * 1024) throw new ArgumentException("Неподдерживаемый или слишком большой аудиофайл: " + info.Name);
            using var metadata = TagLib.File.Create(info.FullName);
            if (metadata.Properties.Duration.TotalSeconds <= 0) throw new ArgumentException("Не удалось прочитать аудио: " + info.Name);
            await using var input = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token)).ToLowerInvariant();
            var destination = Path.Combine(_root, hash + info.Extension.ToLowerInvariant());
            if (!File.Exists(destination))
            {
                var drive = new DriveInfo(Path.GetPathRoot(_root)!);
                if (drive.AvailableFreeSpace < info.Length + 64L * 1024 * 1024) throw new IOException("Недостаточно места для локальной музыки.");
                input.Position = 0;
                var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true)) await input.CopyToAsync(output, token);
                    File.Move(temp, destination, false);
                }
                catch (IOException) when (File.Exists(destination)) { }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            var fallback = Path.GetFileNameWithoutExtension(info.Name).Split(" - ", 2);
            var artist = metadata.Tag.FirstPerformer ?? (fallback.Length == 2 ? fallback[0] : "Локальная музыка");
            var title = metadata.Tag.Title ?? fallback[^1];
            var lyrics = metadata.Tag.Lyrics;
            var lrc = Path.ChangeExtension(info.FullName, ".lrc");
            if (string.IsNullOrWhiteSpace(lyrics) && File.Exists(lrc) && new FileInfo(lrc).Length <= 256 * 1024) lyrics = await File.ReadAllTextAsync(lrc, token);
            if (!string.IsNullOrWhiteSpace(lyrics) && lyrics.Length <= 64000) await File.WriteAllTextAsync(Path.ChangeExtension(destination, ".lyrics.txt"), lyrics, token);
            var picture = metadata.Tag.Pictures.FirstOrDefault();
            string? thumb = null;
            if (picture is { Data.Count: > 0 and <= 524288 } && picture.MimeType is "image/jpeg" or "image/png")
            {
                var cover = Path.Combine(_root, hash + (picture.MimeType == "image/png" ? ".cover.png" : ".cover.jpg"));
                if (!File.Exists(cover)) await File.WriteAllBytesAsync(cover, picture.Data.Data, token);
                thumb = new Uri(cover).AbsoluteUri;
            }
            tracks.Add(new(title, artist, "Локально", new Uri(destination).AbsoluteUri, metadata.Properties.Duration.TotalSeconds, thumb, true, Album: metadata.Tag.Album));
        }
        return tracks.DistinctBy(t => t.Url).ToArray();
    }
}
