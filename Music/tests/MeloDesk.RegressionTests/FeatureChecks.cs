using System.Text.Json;
using MeloDesk.Music;
using MeloDesk.Telegram;
using MeloDesk.UI;
using SkiaSharp;
using SkiaSharp.Skottie;

static class FeatureChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static Task PhotoAndSender()
    {
        using var json = JsonDocument.Parse("""
        {"id":5,"chat_id":9,"sender_id":{"@type":"messageSenderUser","user_id":123},"date":1793100000,"is_outgoing":false,"content":{"@type":"messagePhoto","caption":{"text":"Caption"},"photo":{"sizes":[{"width":90,"height":60,"photo":{"id":7,"size":90}},{"width":640,"height":480,"photo":{"id":8,"size":1234}}]}}}
        """);
        var message = TelegramMessageParser.Parse(json.RootElement);
        Check(message.SenderId == 123 && !message.SenderIsChat && message.Body == "Caption", "Sender or caption lost.");
        Check(message.Attachment is { Kind: "Photo", FileId: 8, ThumbnailId: 8, Width: 640 }, "Photo size/file selection incorrect.");
        return Task.CompletedTask;
    }
    public static Task AttachmentKinds()
    {
        foreach (var (type, container, field, kind) in new[] { ("messageVoiceNote", "voice_note", "voice", "Voice"), ("messageVideo", "video", "video", "Video"), ("messageVideoNote", "video_note", "video", "VideoNote"), ("messageAudio", "audio", "audio", "Audio"), ("messageDocument", "document", "document", "Document"), ("messageAnimation", "animation", "animation", "Animation"), ("messageSticker", "sticker", "sticker", "Sticker") })
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
            { ["@type"] = type, [container] = new Dictionary<string, object> { ["duration"] = 12, ["waveform"] = "AQID", [field] = new { id = 42, size = 1234 } } }));
            var media = TelegramMessageParser.ParseAttachment(json.RootElement);
            Check(media?.Kind == kind && media.FileId == 42 && media.Size == 1234, type + " lost its file.");
            if (kind == "Voice") Check(media!.Waveform?.Length == 3 && media.Duration == 12, "Voice waveform/duration lost.");
        }
        return Task.CompletedTask;
    }
    public static Task PlaylistMerge()
    {
        var playlist = Guid.NewGuid().ToString();
        PlaylistOperation Op(long clock, string kind, OnlineTrack? track = null, string? url = null) => new(Guid.NewGuid().ToString(), clock, kind, playlist, "Mix", track, url);
        var a = new OnlineTrack("A", "Artist", "YouTube", "https://youtube.com/watch?v=A", 5);
        var b = a with { Title = "B", Url = "https://youtube.com/watch?v=B" };
        var create = Op(1, "create"); var addA = Op(2, "add", a); var addB = Op(3, "add", b); var removeA = Op(4, "remove", url: a.Url);
        var docs = new[] { new PlaylistDocument(1, 10, "a", [create, addA]), new PlaylistDocument(1, 10, "b", [create, addB, removeA]), new PlaylistDocument(1, 99, "foreign", [Op(5, "delete")]) };
        var merged = MeloDesk.Music.PlaylistMerge.Merge(10, docs);
        var result = MeloDesk.Music.PlaylistMerge.Materialize(merged);
        Check(result.Single().Tracks.Single().Url == b.Url && merged.Count == 4, "Cross-device merge lost a change or mixed accounts.");
        var delete = Op(6, "delete"); var laterAdd = Op(7, "add", a);
        Check(MeloDesk.Music.PlaylistMerge.Materialize(merged.Concat(new[] { delete, laterAdd })).Count == 0, "A deleted playlist was resurrected.");
        var serialized = JsonSerializer.Serialize(new PlaylistDocument(1, 10, "a", [create, addA]));
        Check(!serialized.Contains("Artwork"), "UI image was serialized to the cloud.");
        return Task.CompletedTask;
    }
    public static async Task PlaylistSync()
    {
        var cloud = new FakePlaylistCloud();
        var rootA = Path.Combine(AppContext.BaseDirectory, "library-a"); var rootB = Path.Combine(AppContext.BaseDirectory, "library-b");
        // Each run gets independent account IDs and local stores.
        var account = DateTime.UtcNow.Ticks; var id = Guid.NewGuid().ToString();
        var first = new PlaylistLibrary(account, cloud, rootA);
        cloud.Fail = true;
        try { await first.ChangeAsync("create", id, "My mix"); } catch (IOException) { }
        Check(first.Pending && first.Playlists.Count(p => !p.IsFavorites) == 1, "Offline change was lost.");
        var reopened = new PlaylistLibrary(account, cloud, rootA); reopened.LoadLocal();
        Check(reopened.Pending && reopened.Playlists.Single(p => !p.IsFavorites).Name == "My mix", "Encrypted local cache did not restore.");
        cloud.Fail = false; await reopened.SyncAsync(true);
        var second = new PlaylistLibrary(account, cloud, rootB); await second.SyncAsync(false);
        Check(second.Playlists.Single(p => !p.IsFavorites).Name == "My mix" && !reopened.Pending, "Second device did not receive playlist.");
        var other = new PlaylistLibrary(account + 1, cloud, rootA); other.LoadLocal(); await other.SyncAsync(false);
        Check(other.Playlists.All(p => p.IsFavorites && p.Tracks.Count == 0), "Another account inherited playlists.");
        await second.ChangeAsync("rename", id, "Renamed"); await reopened.SyncAsync(false);
        Check(reopened.Playlists.Single(p => !p.IsFavorites).Name == "Renamed", "Remote rename not merged.");
    }
    public static async Task Preparation()
    {
        using var queue = new StreamPreparationQueue();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var active = 0; var peak = 0;
        async Task<AudioStream> Resolve(OnlineTrack track, CancellationToken token)
        { Interlocked.Increment(ref calls); var current = Interlocked.Increment(ref active); peak = Math.Max(peak, current); await release.Task.WaitAsync(token); Interlocked.Decrement(ref active); return new(track.Url, new Dictionary<string, string>()); }
        for (var i = 0; i < 8; i++) { var track = new OnlineTrack("T", "A", "YouTube", "https://youtube.com/watch?v=" + i, 3); queue.Prepare(track, Resolve); queue.Prepare(track, Resolve); }
        await Task.Delay(100); Check(calls == 2 && peak == 2, "Preparation was not limited to two concurrent jobs.");
        release.SetResult(); await Task.Delay(150); Check(calls == 8 && peak <= 2, "Preparation duplicated jobs.");
    }
    public static Task Stickers()
    {
        using var bitmap = new SKBitmap(2, 2); bitmap.Erase(SKColors.Red);
        using var webp = bitmap.Encode(SKEncodedImageFormat.Webp, 100);
        Check(MediaImageDecoder.Decode(webp.ToArray()).PixelWidth > 0, "WebP did not decode.");
        using var animation = Animation.Parse("""{"v":"5.5.7","fr":30,"ip":0,"op":30,"w":64,"h":64,"layers":[{"ty":1,"ind":1,"sw":64,"sh":64,"sc":"#ff0000","ks":{"o":{"a":0,"k":100},"r":{"a":0,"k":0},"p":{"a":0,"k":[0,0,0]},"a":{"a":0,"k":[0,0,0]},"s":{"a":0,"k":[100,100,100]}},"ip":0,"op":30,"st":0}]}""");
        using var surface = SKSurface.Create(new SKImageInfo(64, 64)); animation!.Seek(.5); animation.Render(surface.Canvas, new SKRect(0, 0, 64, 64));
        using var image = surface.Snapshot(); using var result = SKBitmap.FromImage(image);
        Check(result.GetPixel(32, 32).Red > 200, "TGS animation rendered no content.");
        return Task.CompletedTask;
    }
}

sealed class FakePlaylistCloud : IPlaylistCloud
{
    private readonly Dictionary<(long, string), PlaylistCloudCopy> _copies = new();
    public bool Fail;
    public Task<IReadOnlyList<PlaylistCloudCopy>> LoadAsync(long accountId)
    { if (Fail) throw new IOException("Offline"); return Task.FromResult<IReadOnlyList<PlaylistCloudCopy>>(_copies.Where(c => c.Key.Item1 == accountId).Select(c => c.Value).ToArray()); }
    public Task<long> SaveAsync(long accountId, long messageId, PlaylistDocument document)
    {
        if (Fail) throw new IOException("Offline");
        var clone = JsonSerializer.Deserialize<PlaylistDocument>(JsonSerializer.Serialize(document))!;
        var id = messageId == 0 ? _copies.Count + 1 : messageId;
        _copies[(accountId, document.DeviceId)] = new(id, clone); return Task.FromResult(id);
    }
}
