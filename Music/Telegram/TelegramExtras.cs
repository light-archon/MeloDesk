using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace MeloDesk.Telegram;

public sealed record TelegramSender(string Name, TelegramChatPhoto? Photo);
public sealed record TelegramFileProgress(int FileId, long Downloaded, long Total, bool Complete);

public sealed partial class TelegramService
{
    private readonly ConcurrentDictionary<long, JsonElement> _users = new();
    private readonly ConcurrentDictionary<string, Task<TelegramSender>> _senders = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _sendResults = new();
    public event Action<TelegramFileProgress>? FileProgress;
    public event Action<long, long, JsonElement>? MessageContentChanged;
    public async Task<long> GetAccountIdAsync() => (await RequestAsync(new() { ["@type"] = "getMe" })).GetProperty("id").GetInt64();
    public Task<string?> GetFileAsync(int fileId, CancellationToken cancellationToken = default) => GetAvatarFileAsync(fileId, cancellationToken);
    public async Task<IReadOnlyList<TelegramMessage>> GetOlderHistoryAsync(long chatId, long fromId)
    {
        var result = await RequestAsync(new() { ["@type"] = "getChatHistory", ["chat_id"] = chatId, ["from_message_id"] = fromId, ["offset"] = 0, ["limit"] = 50, ["only_local"] = false });
        return result.GetProperty("messages").EnumerateArray().Select(TelegramMessageParser.Parse).OrderBy(m => m.Id).ToArray();
    }

    public Task<TelegramSender> GetSenderAsync(TelegramMessage message)
    {
        var key = (message.SenderIsChat ? "c" : "u") + message.SenderId;
        return _senders.GetOrAdd(key, _ => ResolveSenderAsync(message));
    }
    private async Task<TelegramSender> ResolveSenderAsync(TelegramMessage message)
    {
        if (message.SenderIsChat)
        {
            var chat = await RequestAsync(new() { ["@type"] = "getChat", ["chat_id"] = message.SenderId }).ConfigureAwait(false);
            return new(TelegramMessageParser.S(chat, "title"), chat.TryGetProperty("photo", out var photo) ? TelegramPhotoParser.ParsePhoto(photo) : null);
        }
        if (message.SenderId == 0) return new("Telegram", null);
        var user = _users.TryGetValue(message.SenderId, out var cached) ? cached
            : await RequestAsync(new() { ["@type"] = "getUser", ["user_id"] = message.SenderId }).ConfigureAwait(false);
        var name = (TelegramMessageParser.S(user, "first_name") + " " + TelegramMessageParser.S(user, "last_name")).Trim();
        return new(name.Length == 0 ? "Удалённый аккаунт" : name, user.TryGetProperty("profile_photo", out var avatar) ? TelegramPhotoParser.ParsePhoto(avatar) : null);
    }
    private bool HandleExtendedUpdate(string? type, JsonElement update)
    {
        if (type == "updateUser")
        {
            var user = update.GetProperty("user").Clone();
            var id = user.GetProperty("id").GetInt64();
            _users[id] = user;
            _senders.TryRemove("u" + id, out _);
            return true;
        }
        if (type == "updateFile")
        {
            var file = update.GetProperty("file");
            var local = file.GetProperty("local");
            FileProgress?.Invoke(new((int)TelegramMessageParser.N(file, "id"), TelegramMessageParser.N(local, "downloaded_size"),
                Math.Max(TelegramMessageParser.N(file, "size"), TelegramMessageParser.N(file, "expected_size")), TelegramMessageParser.B(local, "is_downloading_completed")));
        }
        if (type == "updateMessageContent")
        {
            MessageContentChanged?.Invoke(update.GetProperty("chat_id").GetInt64(), update.GetProperty("message_id").GetInt64(), update.GetProperty("new_content").Clone());
            return true;
        }
        if (type is "updateMessageSendSucceeded" or "updateMessageSendFailed")
        {
            var oldId = update.GetProperty("old_message_id").GetInt64();
            var completion = _sendResults.GetOrAdd(oldId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            if (type == "updateMessageSendSucceeded") completion.TrySetResult(update.GetProperty("message").Clone());
            else completion.TrySetException(new InvalidOperationException("Telegram не подтвердил сохранение плейлистов. Попробуй синхронизацию ещё раз."));
            return true;
        }
        return false;
    }

    public async Task<JsonElement> FindLibraryDocumentsAsync(long accountId)
    {
        await RequestAsync(new() { ["@type"] = "createPrivateChat", ["user_id"] = accountId, ["force"] = false }).ConfigureAwait(false);
        return await RequestAsync(new()
        {
            ["@type"] = "searchChatMessages", ["chat_id"] = accountId, ["query"] = "MeloDeskLibraryV1",
            ["from_message_id"] = 0, ["offset"] = 0, ["limit"] = 100, ["filter"] = new Dictionary<string, object> { ["@type"] = "searchMessagesFilterDocument" }
        }).ConfigureAwait(false);
    }
    public async Task<long> SaveLibraryDocumentAsync(long accountId, long messageId, string path)
    {
        if (accountId != await GetAccountIdAsync().ConfigureAwait(false)) throw new InvalidOperationException("Аккаунт изменился. Перезагрузи плейлисты.");
        var content = new Dictionary<string, object>
        {
            ["@type"] = "inputMessageDocument",
            ["document"] = new Dictionary<string, object>
            {
                ["@type"] = "inputDocument", ["document"] = new Dictionary<string, object> { ["@type"] = "inputFileLocal", ["path"] = path },
                ["disable_content_type_detection"] = true
            },
            ["caption"] = new Dictionary<string, object> { ["@type"] = "formattedText", ["text"] = "MeloDeskLibraryV1 · Плейлисты MeloDesk. Этот файл синхронизирует библиотеку между твоими устройствами." }
        };
        var request = new Dictionary<string, object>
        {
            ["@type"] = messageId == 0 ? "sendMessage" : "editMessageMedia", ["chat_id"] = accountId, ["input_message_content"] = content
        };
        if (messageId != 0) request["message_id"] = messageId;
        var result = await RequestAsync(request).ConfigureAwait(false);
        if (result.TryGetProperty("sending_state", out var state) && state.ValueKind == JsonValueKind.Object)
        {
            var oldId = result.GetProperty("id").GetInt64();
            var completion = _sendResults.GetOrAdd(oldId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            try { result = await completion.Task.WaitAsync(TimeSpan.FromMinutes(2), _cancellation.Token).ConfigureAwait(false); }
            finally { _sendResults.TryRemove(oldId, out _); }
        }
        return result.GetProperty("id").GetInt64();
    }
}
