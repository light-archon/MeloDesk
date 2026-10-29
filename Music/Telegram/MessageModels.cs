using System.ComponentModel;
using System.Text.Json;
using System.Windows.Media;

namespace MeloDesk.Telegram;

public sealed record TelegramAttachment(string Kind, int FileId, string Name, long Size, string? LocalPath,
    int ThumbnailId = 0, string? ThumbnailPath = null, byte[]? MiniPreview = null, int Duration = 0,
    int Width = 0, int Height = 0, byte[]? Waveform = null, string? Format = null)
{
    public string SizeText => Size < 1024 ? $"{Size} Б" : Size < 1048576 ? $"{Size / 1024d:0.#} КБ" : $"{Size / 1048576d:0.#} МБ";
    public string DurationText => TimeSpan.FromSeconds(Duration).ToString(Duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    public bool IsVisual => Kind is "Photo" or "Video" or "VideoNote" or "Animation" or "Sticker";
    public bool IsPlayable => Kind is "Video" or "VideoNote" or "Animation" or "Voice" or "Audio";
    public string Label => Kind switch { "Photo" => "Фотография", "Video" => "Видео", "VideoNote" => "Видеосообщение", "Voice" => "Голосовое сообщение", "Audio" => "Аудиозапись", "Animation" => "Анимация", "Sticker" => "Стикер", _ => Name };
}

public sealed record TelegramMessage(long Id, string Body, string Time, bool IsOutgoing) : INotifyPropertyChanged
{
    public long ChatId { get; init; }
    public long SenderId { get; init; }
    public bool SenderIsChat { get; init; }
    public string Signature { get; init; } = "";
    public string Date { get; init; } = "";
    private bool _showDate;
    public bool ShowDate { get => _showDate; set { _showDate = value; PropertyChanged?.Invoke(this, new(nameof(ShowDate))); } }
    public TelegramAttachment? Attachment { get; init; }
    public ChatAvatar SenderAvatar { get; init; } = new();
    private string _senderName = "";
    public string SenderName { get => _senderName; set { _senderName = value; PropertyChanged?.Invoke(this, new(nameof(SenderName))); PropertyChanged?.Invoke(this, new(nameof(SenderInitial))); } }
    public string SenderInitial => string.IsNullOrWhiteSpace(SenderName) ? "?" : SenderName[..1].ToUpperInvariant();
    public string SenderColor => new[] { "#5EC5CB", "#D4A561", "#8ABC70", "#B99AE9", "#6AB3E5", "#E58EA2" }[(int)((ulong)SenderId % 6)];
    public string Alignment => IsOutgoing ? "Right" : "Left";
    public string BubbleColor => IsOutgoing ? "#2B5278" : "#182533";
    public string Preview => (Attachment?.Label is { } label ? label + (Body.Length > 0 ? ": " : "") : "") + Body;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class TelegramMessageParser
{
    public static TelegramMessage Parse(JsonElement message)
    {
        var content = message.GetProperty("content");
        var type = S(content, "@type");
        var body = type == "messageText" ? Text(content, "text") : Text(content, "caption");
        var sender = message.TryGetProperty("sender_id", out var senderValue) ? senderValue : default;
        var isChat = S(sender, "@type") == "messageSenderChat";
        var attachment = ParseAttachment(content);
        if (attachment == null && type != "messageText") body = ServiceText(content, type);
        var date = DateTimeOffset.FromUnixTimeSeconds(N(message, "date")).ToLocalTime();
        return new(N(message, "id"), body, date.ToString("HH:mm"), B(message, "is_outgoing"))
        {
            ChatId = N(message, "chat_id"), SenderId = N(sender, isChat ? "chat_id" : "user_id"), SenderIsChat = isChat,
            Signature = S(message, "author_signature"), Date = date.ToString("dd MMMM yyyy"), Attachment = attachment
        };
    }

    public static TelegramAttachment? ParseAttachment(JsonElement content)
    {
        var type = S(content, "@type");
        if (type == "messagePhoto" && content.TryGetProperty("photo", out var photo) && photo.TryGetProperty("sizes", out var sizes))
        {
            var all = sizes.EnumerateArray().OrderBy(size => N(size, "width") * N(size, "height")).ToArray();
            if (all.Length == 0) return null;
            var full = all.Last();
            var small = all.FirstOrDefault(size => N(size, "width") >= 320);
            if (small.ValueKind == JsonValueKind.Undefined) small = full;
            var file = full.GetProperty("photo");
            var thumbnail = small.GetProperty("photo");
            return Make("Photo", file, "Фото.jpg", photo, width: (int)N(full, "width"), height: (int)N(full, "height")) with
            { ThumbnailId = (int)N(thumbnail, "id"), ThumbnailPath = PathOf(thumbnail) };
        }
        var fields = type switch
        {
            "messageVideo" => ("Video", "video", "video"), "messageVideoNote" => ("VideoNote", "video_note", "video"),
            "messageVoiceNote" => ("Voice", "voice_note", "voice"), "messageAudio" => ("Audio", "audio", "audio"),
            "messageAnimation" => ("Animation", "animation", "animation"), "messageSticker" => ("Sticker", "sticker", "sticker"),
            "messageDocument" => ("Document", "document", "document"), _ => ("", "", "")
        };
        if (fields.Item1.Length == 0 || !content.TryGetProperty(fields.Item2, out var media) || !media.TryGetProperty(fields.Item3, out var mediaFile)) return null;
        var name = S(media, "file_name");
        if (name.Length == 0) name = fields.Item1 switch { "Voice" => "Голосовое.ogg", "VideoNote" => "Видеосообщение.mp4", "Sticker" => "Стикер", _ => fields.Item1 };
        return Make(fields.Item1, mediaFile, name, media, (int)N(media, "duration"), (int)N(media, "width"), (int)N(media, "height"));
    }

    private static TelegramAttachment Make(string kind, JsonElement file, string name, JsonElement media, int duration = 0, int width = 0, int height = 0)
    {
        var thumb = media.TryGetProperty("thumbnail", out var t) && t.ValueKind == JsonValueKind.Object && t.TryGetProperty("file", out var f) ? f : default;
        return new(kind, (int)N(file, "id"), name, N(file, "size"), PathOf(file), (int)N(thumb, "id"), PathOf(thumb),
            Bytes(media.TryGetProperty("minithumbnail", out var mini) ? mini : default, "data"), duration, width, height,
            Bytes(media, "waveform"), media.TryGetProperty("format", out var format) ? S(format, "@type") : null);
    }
    public static string? PathOf(JsonElement file) => file.ValueKind == JsonValueKind.Object && file.TryGetProperty("local", out var local) && B(local, "is_downloading_completed") ? S(local, "path") : null;
    public static string S(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    public static long N(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number) ? number : 0;
    public static bool B(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var text) ? S(text, "text") : "";
    private static byte[]? Bytes(JsonElement value, string name) { try { var text = S(value, name); return text.Length > 0 ? Convert.FromBase64String(text) : null; } catch (FormatException) { return null; } }
    private static string ServiceText(JsonElement value, string type) => type switch
    {
        "messageChatChangeTitle" => "Название чата изменено: " + S(value, "title"), "messageChatChangePhoto" => "Фотография чата обновлена",
        "messageChatDeletePhoto" => "Фотография чата удалена", "messagePinMessage" => "Закреплено сообщение", "messageChatJoinByLink" => "Вступил по ссылке",
        "messageChatAddMembers" => "Добавлены участники", "messageChatDeleteMember" => "Участник покинул чат", "messageCall" => "Звонок",
        "messagePoll" => value.TryGetProperty("poll", out var poll) ? "Опрос: " + (poll.TryGetProperty("question", out var q) && q.ValueKind == JsonValueKind.Object ? S(q, "text") : S(poll, "question")) : "Опрос",
        "messageLocation" or "messageVenue" => "Геопозиция", "messageContact" => "Контакт", _ => "Событие Telegram"
    };
}
