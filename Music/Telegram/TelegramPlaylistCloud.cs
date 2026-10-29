using System.IO;
using System.Text.Json;
using MeloDesk.Telegram;

namespace MeloDesk.Music;

public sealed class TelegramPlaylistCloud(TelegramService telegram) : IPlaylistCloud
{
    public const string FileName = "musigram-library-v1.json";
    public async Task<IReadOnlyList<PlaylistCloudCopy>> LoadAsync(long accountId)
    {
        var result = await telegram.FindLibraryDocumentsAsync(accountId);
        var copies = new List<PlaylistCloudCopy>();
        foreach (var message in result.GetProperty("messages").EnumerateArray())
        {
            var content = message.GetProperty("content");
            if (!content.TryGetProperty("document", out var doc) || TelegramMessageParser.S(doc, "file_name") != FileName) continue;
            var file = doc.GetProperty("document");
            if (TelegramMessageParser.N(file, "size") > 2_000_000) continue;
            var path = await telegram.GetFileAsync((int)TelegramMessageParser.N(file, "id"));
            if (path == null || new FileInfo(path).Length > 2_000_000) continue;
            try
            {
                var data = JsonSerializer.Deserialize<PlaylistDocument>(await File.ReadAllBytesAsync(path));
                if (data is { Version: 1, Operations: not null } && data.AccountId == accountId && data.Operations.Count <= 5000 && Guid.TryParse(data.DeviceId, out _))
                    copies.Add(new(message.GetProperty("id").GetInt64(), data));
            }
            catch (JsonException) { }
        }
        return copies;
    }
    public async Task<long> SaveAsync(long accountId, long messageId, PlaylistDocument document)
    {
        var directory = Path.Combine(AppPaths.Root, "accounts", accountId.ToString(), "sync");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        if (bytes.Length > 2_000_000) throw new InvalidOperationException("Библиотека слишком велика для этой версии синхронизации.");
        await File.WriteAllBytesAsync(path, bytes);
        try { return await telegram.SaveLibraryDocumentAsync(accountId, messageId, path); }
        finally { File.Delete(path); }
    }
}
