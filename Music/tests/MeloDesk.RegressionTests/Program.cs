using MeloDesk.Music;
using MeloDesk.Telegram;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

if (args.Contains("--recommendation-live")) { await RecommendationChecks.Live(); return; }

var tests = new (string Name, Func<Task> Run)[]
{
    ("Volume ramp is smooth, monotonic and retargets without jumps", () => {
        var ramp = new VolumeRamp(); ramp.Reset(20); ramp.Retarget(80, 1000);
        var values = Enumerable.Range(0, 181).Select(n => ramp.ValueAt(1000 + n)).ToArray();
        if (values[0] != 20 || values[^1] != 80 || values.Zip(values.Skip(1)).Any(p => p.First > p.Second)) throw new Exception("Volume ramp is not monotonic or misses endpoints.");
        var before = ramp.ValueAt(1090); ramp.Retarget(0, 1090);
        if (Math.Abs(ramp.ValueAt(1090) - before) > .001 || ramp.ValueAt(1270) != 0 || !ramp.Finished(1270)) throw new Exception("Volume retarget jumped or failed to finish.");
        return Task.CompletedTask;
    }),
    ("Queue shuffle, repeat modes, manual Next and disliked filtering", TransferPlayerChecks.Queue),
    ("Equalizer and player preferences persist and isolate accounts", TransferPlayerChecks.Preferences),
    ("Local audio copies safely, persists and stays out of cloud documents", TransferPlayerChecks.LocalFiles),
    ("Playlist export formats preserve metadata and reject wrong recordings", TransferPlayerChecks.FormatsAndMatching),
    ("Import preview commits only confirmed songs and retains unresolved entries", TransferPlayerChecks.PreviewCommit),
    ("Import matching retains uncertain recordings until an explicit version choice", TransferPlayerChecks.ResolveMatches),
    ("All genre cards open real source playlists with an independent cache", GenreChecks.Catalogue),
    ("Genre playlists retry failures and keep continuation scoped to the genre", GenreChecks.PagesAndRetry),
    ("Search discovery keeps real release metadata, filters shelves and caches independently", DiscoveryChecks.ReleasesAndCache),
    ("Discovery failures remain retryable and cancellation prevents stale completion", DiscoveryChecks.FailureAndCancellation),
    ("Favorite artists persist, remain idempotent and isolate accounts", ArtistPreferenceChecks.Persistence),
    ("Five-artist onboarding persists and isolates accounts", RecommendationChecks.Onboarding),
    ("Recommendations combine affinities, diversify albums and cache per account", RecommendationChecks.RankingAndCache),
    ("Recommendation offline fallback, changed profiles and cancellation stay isolated", RecommendationChecks.FailureAndCancellation),
    ("Recommendation parsing selects only related artists and available albums", RecommendationChecks.Parsing),
    ("Creating a playlist retains its first track; Favorites persist and isolate accounts", LibraryFeatureChecks.FavoritesAndCreation),
    ("Artist songs paginate beyond 20; albums and their tracks remain accessible", LibraryFeatureChecks.Discography),
    ("YouTube Music catalogue preserves artist IDs, audience, rounded plays and unavailable statistics", CatalogChecks.Metadata),
    ("Late resolution cannot replace the latest selection", LatestWins),
    ("Switch silences old playback and ignores retired player events", StopAndStaleEvents),
    ("Rapid selection commits only the final track", RapidSwitches),
    ("A late automatic advance cannot replace a manually selected track", LateAutoAdvance),
    ("Stopping a pending selection cannot start audio after cancellation", StopLoading),
    ("Loopback streaming preserves ranges, source headers and HLS segment URLs", RelayChecks.Streaming),
    ("Local playlists persist without a server and remain separate per account", RelayChecks.LocalPlaylists),
    ("Shared preparation survives cancellation of one listener", SharedCache),
    ("Abandoned resolution is cancelled", CancelUnusedResolution),
    ("Expired and invalidated addresses are resolved again", CacheExpiry),
    ("Phone and application configuration validation", PhoneValidation),
    ("Native playback switching, pause and stale generation guard", NativeSwitch),
    ("Preparation and repeated switches preserve the Windows mixer and MeloDesk session name", MixerChecks.Switching),
    ("Prebuffering leaves the active track intact and resumes the selected track", NativePrebuffer),
    ("HTTP preparation uses a complete reusable file and rejects partial downloads", BufferChecks.Download),
    ("The selected Telegram session remains stable across restarts and upgrades", BufferChecks.SessionLocation),
    ("A prepared remote track plays and seeks from a local buffer", NativeRemotePrebuffer),
    ("Telegram avatar metadata selects the downloadable small photo", AvatarMetadata),
    ("Downloaded avatar loads without locking the cache file", AvatarDownload),
    ("A late old photo cannot replace the new avatar", AvatarReplacement),
    ("Broken preview does not prevent loading the full photo", BrokenAvatarPreview),
    ("Avatar survives message updates and clears after photo removal", AvatarPersistence),
    ("Message parser preserves sender, caption and photo files", FeatureChecks.PhotoAndSender),
    ("All attachment kinds retain original downloadable files", FeatureChecks.AttachmentKinds),
    ("Cross-device playlist operations merge without lost additions", FeatureChecks.PlaylistMerge),
    ("Playlist sync isolates accounts and survives offline changes", FeatureChecks.PlaylistSync),
    ("Preparation is bounded and does not duplicate work", FeatureChecks.Preparation),
    ("WebP decoding and TGS rendering produce real image frames", FeatureChecks.Stickers)
};
foreach (var (name, run) in tests)
{
    await run();
    Console.WriteLine("PASS " + name);
}
Console.WriteLine($"{tests.Length} regression checks passed.");

static OnlineTrack Track(string id) => new(id, "Test", "YouTube", "https://example.test/" + id, 4);
static AudioStream Stream(string id) => new("https://stream.example.test/" + id, new Dictionary<string, string>());
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

static async Task StopLoading()
{
    var resolver = new ControlledResolver(); var output = new FakeOutput();
    using var playback = new TrackPlaybackController(resolver, output);
    var track = Track("Cancelled"); var selection = playback.SelectAsync(track);
    playback.Stop(); resolver.Complete(track); await selection;
    Check(playback.Current?.State == TrackPlaybackState.Stopped && output.Started.Count == 0, "A cancelled loading request started later.");
}

static async Task LatestWins()
{
    var resolver = new ControlledResolver();
    var output = new FakeOutput();
    using var playback = new TrackPlaybackController(resolver, output);
    var a = Track("A"); var b = Track("B");
    var first = playback.SelectAsync(a);
    var second = playback.SelectAsync(b);
    resolver.Complete(b); await second;
    resolver.Complete(a); await first;
    Check(output.Started.SequenceEqual(new[] { b.Url }), "A stale request was committed after B.");
    Check(playback.Current?.Track == b && playback.Current.State == TrackPlaybackState.Playing, "State does not describe B.");
}

static async Task LateAutoAdvance()
{
    var resolver = new ControlledResolver(); var output = new FakeOutput();
    using var playback = new TrackPlaybackController(resolver, output);
    var a = Track("AutoA"); var b = Track("ManualB"); var c = Track("LateAutoC");
    var first = playback.SelectAsync(a); resolver.Complete(a); await first;
    var endedRequest = playback.Current!.RequestId;
    var manual = playback.SelectAsync(b); resolver.Complete(b); await manual;
    await playback.SelectIfCurrentAsync(c, endedRequest).WaitAsync(TimeSpan.FromSeconds(1));
    Check(playback.Current?.Track == b && output.ActiveUrl == b.Url, "An expired automatic next action replaced the user's selection.");
}

static async Task StopAndStaleEvents()
{
    var resolver = new ControlledResolver(); var output = new FakeOutput();
    using var playback = new TrackPlaybackController(resolver, output);
    var a = Track("A"); var b = Track("B");
    var first = playback.SelectAsync(a); resolver.Complete(a); await first;
    var oldGeneration = output.Generation;
    var next = playback.SelectAsync(b);
    Check(!output.IsPlaying && output.ActiveUrl == null, "Old audio remained active while B loaded.");
    foreach (var signal in new[] { AudioOutputSignal.Playing, AudioOutputSignal.Ended, AudioOutputSignal.Error }) output.Emit(oldGeneration, signal);
    Check(playback.Current?.Track == b && playback.Current.State == TrackPlaybackState.Loading, "A retired native event changed B.");
    resolver.Complete(b); await next;
    Check(output.ActiveUrl == b.Url && playback.Current?.State == TrackPlaybackState.Playing, "B failed to become active.");
}

static async Task RapidSwitches()
{
    var resolver = new ControlledResolver(); var output = new FakeOutput();
    using var playback = new TrackPlaybackController(resolver, output);
    var tracks = Enumerable.Range(0, 20).Select(index => Track(index.ToString())).ToArray();
    var requests = tracks.Select(playback.SelectAsync).ToArray();
    foreach (var track in tracks.Reverse()) resolver.Complete(track);
    await Task.WhenAll(requests);
    Check(output.Started.SequenceEqual(new[] { tracks[^1].Url }), "Rapid switching started an intermediate selection.");
}

static async Task SharedCache()
{
    var calls = 0;
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var completion = new TaskCompletionSource<AudioStream>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var cache = new StreamResolutionCache(async (_, token) => { Interlocked.Increment(ref calls); started.TrySetResult(); return await completion.Task.WaitAsync(token); });
    using var cancellation = new CancellationTokenSource();
    var track = Track("shared");
    var first = cache.ResolveAudioAsync(track, cancellation.Token);
    var second = cache.ResolveAudioAsync(track);
    await started.Task;
    cancellation.Cancel();
    try { await first; throw new Exception("Cancelled listener completed."); } catch (OperationCanceledException) { }
    completion.SetResult(Stream("shared"));
    await second;
    await cache.ResolveAudioAsync(track);
    Check(calls == 1, "A shared or cached stream was extracted more than once.");
}

static async Task CancelUnusedResolution()
{
    var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var cache = new StreamResolutionCache(async (_, token) =>
    {
        started.SetResult();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        return Stream("unreachable");
    });
    using var cancellation = new CancellationTokenSource();
    var request = cache.ResolveAudioAsync(Track("cancel"), cancellation.Token);
    await started.Task;
    cancellation.Cancel();
    try { await request; } catch (OperationCanceledException) { }
    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
}

static async Task CacheExpiry()
{
    var now = DateTimeOffset.UtcNow; var calls = 0;
    using var cache = new StreamResolutionCache((_, _) => { calls++; return Task.FromResult(Stream(calls.ToString())); }, () => now);
    var track = Track("expiry");
    await cache.ResolveAudioAsync(track); await cache.ResolveAudioAsync(track);
    Check(calls == 1, "Cache miss before expiry.");
    now = now.AddMinutes(6);
    await cache.ResolveAudioAsync(track);
    Check(calls == 2, "Expired signed address was reused.");
    cache.Invalidate(track); await cache.ResolveAudioAsync(track);
    Check(calls == 3, "Failed address was not invalidated.");
}

static Task PhoneValidation()
{
    Check(TelegramPhoneNumber.Normalize("+44 (7700) 900-123") == "+447700900123", "Phone formatting failed.");
    Check(TelegramPhoneNumber.Normalize("447700900123") == "+447700900123", "Country code normalization failed.");
    foreach (var bad in new[] { "abc123456789", "123", "+1234567890123456" })
    {
        try { TelegramPhoneNumber.Normalize(bad); throw new Exception("Invalid phone accepted."); } catch (ArgumentException) { }
    }
    Check(!TelegramSettings.IsValid(new TelegramCredentials(1, new string('x', 32))), "Invalid hash accepted.");
    Check(TelegramSettings.IsValid(new TelegramCredentials(1, new string('a', 32))), "Valid configuration rejected.");
    return Task.CompletedTask;
}

static async Task NativeSwitch()
{
    var folder = Path.Combine(AppContext.BaseDirectory, "test-audio");
    Directory.CreateDirectory(folder);
    var pathA = Path.Combine(folder, "a.wav"); var pathB = Path.Combine(folder, "b.wav");
    WriteWave(pathA, 440); WriteWave(pathB, 880);
    using var output = new AudioPlayback { Volume = 0 };
    var first = output.BeginSwitch();
    Check(output.Start(new AudioStream(new Uri(pathA).AbsoluteUri, new Dictionary<string, string>()), first), "Native A start failed.");
    await Until(() => output.IsPlaying && output.Time > 500);
    var next = output.BeginSwitch();
    Check(!output.IsPlaying, "Native old session still exposed after switch.");
    Check(!output.Start(new AudioStream(new Uri(pathA).AbsoluteUri, new Dictionary<string, string>()), first), "Native output accepted a stale generation.");
    var timer = Stopwatch.StartNew();
    Check(output.Start(new AudioStream(new Uri(pathB).AbsoluteUri, new Dictionary<string, string>()), next), "Native B start failed.");
    await Until(() => output.IsPlaying && output.Time > 500);
    Console.WriteLine($"Native B active in {timer.ElapsedMilliseconds} ms including 500 ms of decoded playback.");
    output.SetPaused(true); output.SetPaused(true);
    await Until(() => !output.IsPlaying);
    output.SetPaused(false); await Until(() => output.IsPlaying);
    output.Dispose();
    File.Delete(pathA); File.Delete(pathB);
}

static Task AvatarMetadata()
{
    using var document = JsonDocument.Parse("""
    {"small":{"id":42,"local":{"path":"not-ready.jpg","is_downloading_completed":false}},"minithumbnail":{"data":"AQID"},"has_animation":true}
    """);
    var photo = TelegramPhotoParser.ParsePhoto(document.RootElement);
    Check(photo?.FileId == 42 && photo.LocalPath == null, "Incomplete avatar was mistaken for a local file.");
    Check(photo!.HasAnimation && photo.Minithumbnail!.SequenceEqual(new byte[] { 1, 2, 3 }), "Avatar preview metadata was lost.");
    using var empty = JsonDocument.Parse("null");
    Check(TelegramPhotoParser.ParsePhoto(empty.RootElement) == null, "Missing photo did not preserve initials fallback.");
    return Task.CompletedTask;
}

static async Task NativePrebuffer()
{
    var folder = Path.Combine(AppContext.BaseDirectory, "test-audio"); Directory.CreateDirectory(folder);
    var firstPath = Path.Combine(folder, "warm-a.wav"); var secondPath = Path.Combine(folder, "warm-b.wav");
    WriteWave(firstPath, 440); WriteWave(secondPath, 880);
    using var output = new AudioPlayback { Volume = 0 };
    var a = new AudioStream(new Uri(firstPath).AbsoluteUri, new Dictionary<string, string>());
    var b = new AudioStream(new Uri(secondPath).AbsoluteUri, new Dictionary<string, string>());
    var signals = new ConcurrentQueue<long>(); output.Signal += (generation, _) => signals.Enqueue(generation);
    var active = output.BeginSwitch(); output.Start(a, active); await Until(() => output.Time > 200);
    await output.PrepareAsync(b);
    Check(output.IsPlaying && signals.All(g => g == active), "Prebuffer changed active playback or emitted its own session events.");
    var selected = output.BeginSwitch(); Check(output.Start(b, selected), "Prepared track did not start.");
    await Until(() => output.IsPlaying && output.Time > 500);
    Check(output.Time < 2000, "Prepared track resumed from the middle.");
    output.Dispose(); File.Delete(firstPath); File.Delete(secondPath);
}

static async Task NativeRemotePrebuffer()
{
    var folder = Path.Combine(AppContext.BaseDirectory, "test-audio"); Directory.CreateDirectory(folder);
    var file = Path.Combine(folder, "remote.wav"); WriteWave(file, 660);
    await using var server = new AudioTestServer(File.ReadAllBytes(file));
    var stream = new AudioStream(server.Url, new Dictionary<string, string>());
    using var output = new AudioPlayback { Volume = 0 };
    await output.PrepareAsync(stream);
    var generation = output.BeginSwitch();
    Check(output.Start(stream, generation), "Buffered remote file did not start.");
    await Until(() => output.Time > 500);
    Check(output.IsSeekable, "Buffered remote track cannot be seeked.");
    output.SetPaused(true); await Until(() => !output.IsPlaying);
    output.Position = .2f; output.SetPaused(false);
    await Until(() => output.IsPlaying && output.Time > 900);
    Check(server.Requests == 1, "Prepared playback reopened the remote source.");
    output.Dispose(); File.Delete(file);
}

static async Task AvatarDownload()
{
    var path = AvatarFile("download", Colors.Red);
    var calls = 0;
    var loader = new ChatAvatarLoader((id, _) => { Check(id == 42, "Wrong avatar file requested."); calls++; return Task.FromResult<string?>(path); }, action => action());
    var avatar = new ChatAvatar();
    await loader.LoadAsync(avatar, new TelegramChatPhoto(42, null, null, false));
    Check(calls == 1 && AvatarPixel(avatar) == Colors.Red, "Downloaded avatar was not displayed.");
    File.Delete(path);
    Check(!File.Exists(path), "Avatar decoder locked the TDLib cache file.");
}

static async Task AvatarReplacement()
{
    var oldPath = AvatarFile("old", Colors.Red); var newPath = AvatarFile("new", Colors.Blue);
    var oldDownload = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var loader = new ChatAvatarLoader((id, _) => id == 1 ? oldDownload.Task : Task.FromResult<string?>(newPath), action => action());
    var avatar = new ChatAvatar();
    var old = loader.LoadAsync(avatar, new TelegramChatPhoto(1, null, null, false));
    await loader.LoadAsync(avatar, new TelegramChatPhoto(2, null, null, false));
    oldDownload.SetResult(oldPath); await old;
    Check(AvatarPixel(avatar) == Colors.Blue, "Old download replaced the current photo.");
    File.Delete(oldPath); File.Delete(newPath);
}

static async Task BrokenAvatarPreview()
{
    var path = AvatarFile("preview", Colors.Green);
    var loader = new ChatAvatarLoader((_, _) => Task.FromResult<string?>(path), action => action());
    var avatar = new ChatAvatar();
    await loader.LoadAsync(avatar, new TelegramChatPhoto(1, null, new byte[] { 0, 1, 2, 3 }, false));
    Check(AvatarPixel(avatar) == Colors.Green, "An invalid minithumbnail prevented the full photo.");
    File.Delete(path);
}

static async Task AvatarPersistence()
{
    var path = AvatarFile("persistent", Colors.Blue);
    var loader = new ChatAvatarLoader((_, _) => Task.FromResult<string?>(path), action => action());
    var chat = new TelegramChat(1, "Test", "Before", "10:00", 0, true, new TelegramChatPhoto(1, path, null, false));
    await loader.LoadAsync(chat.Avatar, chat.Photo);
    var updated = chat with { Preview = "After" };
    Check(ReferenceEquals(updated.Avatar, chat.Avatar) && AvatarPixel(updated.Avatar) == Colors.Blue, "A message update reset the avatar.");
    await loader.LoadAsync(updated.Avatar, null);
    Check(updated.Avatar.Image == null, "Deleted chat photo did not return to initials fallback.");
    File.Delete(path);
}

static string AvatarFile(string name, Color color)
{
    var folder = Path.Combine(AppContext.BaseDirectory, "test-avatars");
    Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, name + ".png");
    var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { color.B, color.G, color.R, color.A }, 4);
    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var file = File.Create(path); encoder.Save(file);
    return path;
}
static Color AvatarPixel(ChatAvatar avatar)
{
    Check(avatar.Image is BitmapSource, "Avatar image is missing.");
    var image = new FormatConvertedBitmap((BitmapSource)avatar.Image!, PixelFormats.Bgra32, null, 0);
    var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
    image.CopyPixels(bytes, image.PixelWidth * 4, 0);
    return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
}

static async Task Until(Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(8);
    while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Native playback did not reach expected state."); await Task.Delay(50); }
}
static void WriteWave(string path, int frequency)
{
    const int rate = 22050, samples = rate * 4;
    using var writer = new BinaryWriter(File.Create(path));
    writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
    writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
    writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
    for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(2 * Math.PI * frequency * i / rate) * 1000));
}

sealed class ControlledResolver : IAudioStreamResolver
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AudioStream>> _requests = new();
    public Task<AudioStream> ResolveAudioAsync(OnlineTrack track, CancellationToken cancellationToken = default)
        => _requests.GetOrAdd(track.Url, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    public void Complete(OnlineTrack track) => _requests[track.Url].SetResult(new AudioStream(track.Url, new Dictionary<string, string>()));
    public void Invalidate(OnlineTrack track) { }
}

sealed class FakeOutput : IAudioOutput
{
    public event Action<long, AudioOutputSignal>? Signal;
    public List<string> Started { get; } = [];
    public string? ActiveUrl { get; private set; }
    public long Generation { get; private set; }
    public bool IsPlaying { get; private set; }
    public long BeginSwitch() { ActiveUrl = null; IsPlaying = false; return ++Generation; }
    public bool Start(AudioStream stream, long generation)
    {
        if (generation != Generation) return false;
        Started.Add(stream.Url); ActiveUrl = stream.Url; IsPlaying = true;
        Signal?.Invoke(generation, AudioOutputSignal.Playing);
        return true;
    }
    public void Emit(long generation, AudioOutputSignal signal) => Signal?.Invoke(generation, signal);
    public void SetPaused(bool paused) { IsPlaying = !paused; Signal?.Invoke(Generation, paused ? AudioOutputSignal.Paused : AudioOutputSignal.Playing); }
    public bool IsSeekable => true;
    public long Time => 0;
    public long Length => 0;
    public float Position { get; set; }
    public int Volume { get; set; }
    public void Dispose() { ActiveUrl = null; IsPlaying = false; }
}
