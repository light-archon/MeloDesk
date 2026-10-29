using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Concurrent;

namespace MeloDesk.Telegram;

public sealed partial class TelegramService : IDisposable
{
    private readonly int _apiId;
    private readonly string _apiHash;
    private readonly string _databaseRoot;
    private readonly CancellationTokenSource _cancellation = new();
    private int _clientId;
    private Task? _receiveTask;
    private readonly object _closeSync = new();
    private Task? _closeTask;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _requests = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<int, string> _avatarPaths = new();
    private readonly ConcurrentDictionary<int, Lazy<Task<string?>>> _avatarDownloads = new();
    private readonly SemaphoreSlim _avatarSlots = new(4);

    public event Action<string, string>? StateChanged;
    public event Action<long, TelegramMessage>? MessageReceived;
    public event Action<long, TelegramChatPhoto?>? ChatPhotoChanged;

    public TelegramService(int apiId, string apiHash, string? databaseRoot = null)
    {
        _apiId = apiId;
        _apiHash = apiHash;
        _databaseRoot = databaseRoot ?? AppPaths.TelegramRoot;
    }

    public static string GetNativeVersion()
    {
        TdJsonNative.td_execute("{\"@type\":\"setLogVerbosityLevel\",\"new_verbosity_level\":1}");
        var response = TdJsonNative.td_execute("{\"@type\":\"getOption\",\"name\":\"version\"}");
        if (response == IntPtr.Zero) return "неизвестна";
        using var document = JsonDocument.Parse(Marshal.PtrToStringUTF8(response)!);
        return document.RootElement.TryGetProperty("value", out var value)
            ? value.GetString() ?? "неизвестна"
            : "неизвестна";
    }

    public void Start()
    {
        _clientId = TdJsonNative.td_create_client_id();
        _receiveTask = Task.Run(ReceiveLoop);
        Send(new Dictionary<string, object> { ["@type"] = "getAuthorizationState" });
    }

    public void SubmitPhone(string phone) => Send(new Dictionary<string, object>
    {
        ["@type"] = "setAuthenticationPhoneNumber", ["phone_number"] = phone
    });

    public void SubmitCode(string code) => Send(new Dictionary<string, object>
    {
        ["@type"] = "checkAuthenticationCode", ["code"] = code
    });

    public void SubmitPassword(string password) => Send(new Dictionary<string, object>
    {
        ["@type"] = "checkAuthenticationPassword", ["password"] = password
    });
    public void SubmitEmail(string email) => Send(new Dictionary<string, object> { ["@type"] = "setAuthenticationEmailAddress", ["email_address"] = email });
    public void SubmitEmailCode(string code) => Send(new Dictionary<string, object>
    {
        ["@type"] = "checkAuthenticationEmailCode",
        ["code"] = new Dictionary<string, object> { ["@type"] = "emailAddressAuthenticationCode", ["code"] = code }
    });

    public async Task<IReadOnlyList<TelegramChat>> GetChatsAsync()
    {
        try { await RequestAsync(new() { ["@type"] = "loadChats", ["chat_list"] = new Dictionary<string, object> { ["@type"] = "chatListMain" }, ["limit"] = 100 }); }
        catch (TelegramApiException error) when (error.Code == 404) { }
        var result = await RequestAsync(new() { ["@type"] = "getChats", ["chat_list"] = new Dictionary<string, object> { ["@type"] = "chatListMain" }, ["limit"] = 100 });
        var ids = result.GetProperty("chat_ids").EnumerateArray().Select(id => id.GetInt64()).ToArray();
        return await Task.WhenAll(ids.Select(async id => ParseChat(await RequestAsync(new() { ["@type"] = "getChat", ["chat_id"] = id }))));
    }

    public async Task<IReadOnlyList<TelegramMessage>> GetHistoryAsync(long chatId)
    {
        await RequestAsync(new() { ["@type"] = "openChat", ["chat_id"] = chatId });
        var collected = new Dictionary<long, TelegramMessage>();
        long fromId = 0;
        for (var page = 0; page < 3 && collected.Count < 50; page++)
        {
            var response = await RequestAsync(new()
            {
                ["@type"] = "getChatHistory", ["chat_id"] = chatId, ["from_message_id"] = fromId,
                ["offset"] = 0, ["limit"] = 50, ["only_local"] = false
            });
            var batch = response.GetProperty("messages").EnumerateArray().Select(ParseMessage).ToArray();
            var previousCount = collected.Count;
            foreach (var item in batch) collected[item.Id] = item;
            if (collected.Count == previousCount) break;
            fromId = batch.Min(m => m.Id);
        }
        var messages = collected.Values.OrderBy(m => m.Id).ToArray();
        if (messages.Length > 0)
            Send(new Dictionary<string, object> { ["@type"] = "viewMessages", ["chat_id"] = chatId, ["message_ids"] = messages.Select(m => m.Id).ToArray(), ["force_read"] = true });
        return messages;
    }
    public void CloseChat(long chatId) => Send(new Dictionary<string, object> { ["@type"] = "closeChat", ["chat_id"] = chatId });

    public async Task<string?> GetAvatarFileAsync(int fileId, CancellationToken cancellationToken = default)
    {
        if (fileId <= 0) return null;
        if (_avatarPaths.TryGetValue(fileId, out var cached) && File.Exists(cached)) return cached;
        var operation = _avatarDownloads.GetOrAdd(fileId, id => new Lazy<Task<string?>>(() => DownloadAvatarAsync(id)));
        try
        {
            var path = await operation.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (path != null && File.Exists(path)) return path;
            _avatarDownloads.TryRemove(new KeyValuePair<int, Lazy<Task<string?>>>(fileId, operation));
            return null;
        }
        catch
        {
            if (operation.IsValueCreated && operation.Value.IsCompleted)
                _avatarDownloads.TryRemove(new KeyValuePair<int, Lazy<Task<string?>>>(fileId, operation));
            throw;
        }
    }

    private async Task<string?> DownloadAvatarAsync(int fileId)
    {
        await _avatarSlots.WaitAsync(_cancellation.Token).ConfigureAwait(false);
        try
        {
            var response = await RequestAsync(new()
            {
                ["@type"] = "downloadFile", ["file_id"] = fileId, ["priority"] = 8,
                ["offset"] = 0, ["limit"] = 0, ["synchronous"] = true
            }, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            var file = TelegramPhotoParser.ParseFile(response);
            if (!file.IsComplete || string.IsNullOrWhiteSpace(file.Path) || !File.Exists(file.Path)) return null;
            _avatarPaths[fileId] = file.Path;
            return file.Path;
        }
        finally { _avatarSlots.Release(); }
    }

    public async Task SendTextAsync(long chatId, string text)
    {
        await RequestAsync(new()
        {
            ["@type"] = "sendMessage", ["chat_id"] = chatId,
            ["input_message_content"] = new Dictionary<string, object>
            {
                ["@type"] = "inputMessageText",
                ["text"] = new Dictionary<string, object> { ["@type"] = "formattedText", ["text"] = text }
            }
        });
    }

    private async Task<JsonElement> RequestAsync(Dictionary<string, object> request, TimeSpan? timeout = null)
    {
        var id = Guid.NewGuid().ToString("N");
        request["@extra"] = id;
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[id] = completion;
        try { Send(request); return await completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(30), _cancellation.Token); }
        finally { _requests.TryRemove(id, out _); }
    }

    private void Send(object request)
    {
        if (_clientId == 0) return;
        TdJsonNative.td_send(_clientId, JsonSerializer.Serialize(request));
    }

    private void ReceiveLoop()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var result = TdJsonNative.td_receive(0.25);
                if (result == IntPtr.Zero) continue;
                using var document = JsonDocument.Parse(Marshal.PtrToStringUTF8(result)!);
                HandleMessage(document.RootElement);
            }
        }
        catch (Exception exception)
        {
            StateChanged?.Invoke("error", $"Ошибка TDLib: {exception.Message}");
        }
    }

    private void HandleMessage(JsonElement message)
    {
        if (message.TryGetProperty("@client_id", out var clientId) && clientId.GetInt32() != _clientId) return;
        var type = message.GetProperty("@type").GetString();
        if (HandleExtendedUpdate(type, message)) return;
        if (type == "updateFile")
        {
            var file = TelegramPhotoParser.ParseFile(message.GetProperty("file"));
            if (file.IsComplete && !string.IsNullOrWhiteSpace(file.Path)) _avatarPaths[file.Id] = file.Path;
            return;
        }
        if (type == "updateChatPhoto")
        {
            var photo = message.TryGetProperty("photo", out var value) ? TelegramPhotoParser.ParsePhoto(value) : null;
            ChatPhotoChanged?.Invoke(message.GetProperty("chat_id").GetInt64(), photo);
            return;
        }
        if (message.TryGetProperty("@extra", out var extra) && extra.ValueKind == JsonValueKind.String && _requests.TryGetValue(extra.GetString()!, out var completion))
        {
            if (type == "error") completion.TrySetException(new TelegramApiException(message.GetProperty("code").GetInt32(), message.GetProperty("message").GetString() ?? "Ошибка Telegram"));
            else completion.TrySetResult(message.Clone());
            return;
        }
        if (type == "updateNewMessage")
        {
            var data = message.GetProperty("message");
            MessageReceived?.Invoke(data.GetProperty("chat_id").GetInt64(), ParseMessage(data));
            return;
        }
        if (type == "error")
        {
            var description = message.TryGetProperty("message", out var error) ? error.GetString() : "Неизвестная ошибка";
            StateChanged?.Invoke("error", description ?? "Неизвестная ошибка");
            return;
        }

        if (type != "updateAuthorizationState") return;
        var state = message.GetProperty("authorization_state").GetProperty("@type").GetString();
        switch (state)
        {
            case "authorizationStateWaitTdlibParameters":
                InitializeTdlib();
                break;
            case "authorizationStateWaitPhoneNumber":
                StateChanged?.Invoke("phone", "Введите номер телефона Telegram");
                break;
            case "authorizationStateWaitCode":
                StateChanged?.Invoke("code", "Введите код подтверждения");
                break;
            case "authorizationStateWaitPassword":
                StateChanged?.Invoke("password", "Введите пароль двухэтапной проверки");
                break;
            case "authorizationStateWaitEmailAddress":
                StateChanged?.Invoke("email", "Введите адрес электронной почты для входа");
                break;
            case "authorizationStateWaitEmailCode":
                StateChanged?.Invoke("emailCode", "Введите код из электронной почты");
                break;
            case "authorizationStateReady":
                StateChanged?.Invoke("ready", "Вход в Telegram выполнен");
                break;
            case "authorizationStateClosed":
                _closed.TrySetResult();
                break;
            default:
                StateChanged?.Invoke("other", $"Состояние авторизации: {state}");
                break;
        }
    }

    private static TelegramChat ParseChat(JsonElement chat)
    {
        var title = chat.GetProperty("title").GetString() ?? "Чат";
        var kind = chat.GetProperty("type").GetProperty("@type").GetString();
        var hasLast = chat.TryGetProperty("last_message", out var last) && last.ValueKind == JsonValueKind.Object;
        var preview = hasLast ? ParseMessage(last).Preview.Replace('\r', ' ').Replace('\n', ' ') : "Нет сообщений";
        var time = hasLast ? DateTimeOffset.FromUnixTimeSeconds(last.GetProperty("date").GetInt64()).ToLocalTime().ToString("HH:mm") : "";
        return new TelegramChat(chat.GetProperty("id").GetInt64(), title, preview, time,
            chat.TryGetProperty("unread_count", out var unread) ? unread.GetInt32() : 0, kind is "chatTypePrivate" or "chatTypeSecret",
            chat.TryGetProperty("photo", out var photo) ? TelegramPhotoParser.ParsePhoto(photo) : null);
    }

    private static TelegramMessage ParseMessage(JsonElement message) => TelegramMessageParser.Parse(message);

    private void InitializeTdlib()
    {
        var basePath = _databaseRoot;
        Directory.CreateDirectory(basePath);
        var keyPath = Path.Combine(basePath, "database-key.dat");
        byte[] databaseKey;
        if (File.Exists(keyPath))
            databaseKey = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), null, DataProtectionScope.CurrentUser);
        else
        {
            databaseKey = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(keyPath, ProtectedData.Protect(databaseKey, null, DataProtectionScope.CurrentUser));
        }
        Send(new Dictionary<string, object>
        {
            ["@type"] = "setTdlibParameters",
            ["database_directory"] = Path.Combine(basePath, "database"),
            ["files_directory"] = Path.Combine(basePath, "files"),
            ["database_encryption_key"] = Convert.ToBase64String(databaseKey),
            ["use_file_database"] = true,
            ["use_chat_info_database"] = true,
            ["use_message_database"] = true,
            ["use_secret_chats"] = true,
            ["api_id"] = _apiId,
            ["api_hash"] = _apiHash,
            ["system_language_code"] = CultureInfo.CurrentUICulture.Name,
            ["device_model"] = "Windows PC",
            ["application_version"] = "0.5.2"
        });
        StateChanged?.Invoke("loading", "Подключение к Telegram…");
    }

    public Task CloseAsync()
    {
        lock (_closeSync) return _closeTask ??= CloseCoreAsync();
    }

    private async Task CloseCoreAsync()
    {
        try
        {
            if (_clientId != 0)
            {
                Send(new Dictionary<string, object> { ["@type"] = "close" });
                await _closed.Task.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }
        }
        finally
        {
            _cancellation.Cancel();
            if (_receiveTask != null) await _receiveTask.ConfigureAwait(false);
            foreach (var request in _requests.Values) request.TrySetCanceled();
            _clientId = 0;
            _cancellation.Dispose();
        }
    }

    public void Dispose() => CloseAsync().GetAwaiter().GetResult();
}

public sealed record TelegramChat(long Id, string Title, string Preview, string Time, int Unread, bool IsPrivate, TelegramChatPhoto? Photo = null)
{
    public ChatAvatar Avatar { get; init; } = new();
    public string Initial => string.IsNullOrWhiteSpace(Title) ? "?" : Title[..1].ToUpperInvariant();
    public string UnreadText => Unread == 0 ? "" : Unread.ToString();
}
public sealed class TelegramApiException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
