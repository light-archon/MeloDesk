using MeloDesk.Music;
using MeloDesk.Telegram;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Net.Http;

namespace MeloDesk;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<TelegramChat> _chats = [];
    private readonly ObservableCollection<TelegramMessage> _messages = [];
    private readonly ObservableCollection<OnlineTrack> _tracks = [];
    private readonly OnlineMusicSearch _search = new();
    private readonly StreamResolutionCache _streams;
    private readonly ChatAvatarLoader _chatAvatars;
    private readonly HttpClient _artworkHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly CancellationTokenSource _shutdown = new();
    private ICollectionView? _chatView;
    private ICollectionView? _trackView;
    private TelegramService? _telegram;
    private AudioPlayback? _audio;
    private TrackPlaybackController? _playback;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _warmupCancellation;
    private string? _warmupTarget;
    private TelegramChat? _selectedChat;
    private OnlineTrack? _currentTrack;
    private OnlineTrack[] _playQueue = [];
    private string _source = "Все";
    private string _authMode = "phoneSetup";
    private string? _pendingPhoneNumber;
    private bool _personalOnly;
    private bool _seeking;
    private bool _closing;
    private bool _updatingChats;
    private bool _loadingHistory;
    private bool _hasOlderHistory = true;
    private UI.TrayService? _tray;
    private bool _audioInitializing;
    public void RestoreFromTray() => _tray?.Restore();
    public TelegramService? Telegram => _telegram;
    public Task SaveSessionAndCloseAsync() => _telegram?.CloseAsync() ?? Task.CompletedTask;
    public void PauseMusicForAttachment() => _playback?.SetPaused(true);

    public MainWindow()
    {
        _streams = new StreamResolutionCache(_search.ResolveAudioAsync);
        _chatAvatars = new ChatAvatarLoader((id, token) => _telegram?.GetAvatarFileAsync(id, token) ?? Task.FromResult<string?>(null), Ui);
        InitializeComponent();
        if (Application.Current is App app) _tray = new UI.TrayService(this, () => app.IsExiting, app.ExitApplication);
        _chatView = CollectionViewSource.GetDefaultView(_chats);
        _chatView.Filter = item => item is TelegramChat chat && (!_personalOnly || chat.IsPrivate)
            && chat.Title.Contains(ChatSearch.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
        ChatList.ItemsSource = _chatView;
        MessagesList.ItemsSource = _messages;
        MessagesList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(MessagesScrolled));
        _trackView = CollectionViewSource.GetDefaultView(_tracks);
        _trackView.Filter = item => item is OnlineTrack track && (_source == "Все" || track.Provider == _source);
        TrackList.ItemsSource = _trackView;
        Loaded += async (_, _) => await InitializeAudioAsync();
        _playbackTimer.Tick += (_, _) => UpdatePlaybackPosition();
        _playbackTimer.Start();
        Closed += OnClosed;
        Loaded += (_, _) => UI.WindowAppearance.UseDarkTitleBar(this);
        InitializeLibraryTimer();
        InitializeTelegram();
    }

    private async Task InitializeAudioAsync()
    {
        if (_closing || _audio != null || _audioInitializing) return;
        _audioInitializing = true;
        try
        {
            var audio = await Task.Run(() => new AudioPlayback());
            if (_closing) { audio.Dispose(); return; }
            _audio = audio;
            _audio.Volume = (int)VolumeSlider.Value;
            _playback = new TrackPlaybackController(_streams, _audio);
            _playback.Changed += snapshot => Ui(() => ApplyPlaybackSnapshot(snapshot));
            PrepareVisibleTracks();
        }
        catch (Exception error) { if (!_closing) MusicStatus.Text = "Не удалось загрузить аудиодвижок: " + error.Message; }
        finally { _audioInitializing = false; }
    }

    private void AllChats_Click(object sender, RoutedEventArgs e) => ShowChats(false);
    private void Personal_Click(object sender, RoutedEventArgs e) => ShowChats(true);
    private void ShowChats(bool personal)
    {
        _personalOnly = personal;
        ChatListPane.Visibility = ConversationPane.Visibility = Visibility.Visible;
        MusicView.Visibility = Visibility.Collapsed;
        _chatView?.Refresh();
        SetRail(personal ? PersonalButton : AllChatsButton);
    }
    private void Music_Click(object sender, RoutedEventArgs e)
    {
        ChatListPane.Visibility = ConversationPane.Visibility = Visibility.Collapsed;
        MusicView.Visibility = Visibility.Visible;
        SetRail(MusicButton);
        MusicSearchBox.Focus();
    }
    private void SetRail(Button selected)
    {
        foreach (var button in new[] { AllChatsButton, PersonalButton, MusicButton })
        {
            button.Background = Brush(button == selected ? "#20384C" : "#0E1621");
            button.Foreground = Brush(button == selected ? "#65B9F4" : "#7E96AA");
        }
    }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private void ChatSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ChatSearchHint != null) ChatSearchHint.Visibility = ChatSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _chatView?.Refresh();
    }

    private void InitializeTelegram()
    {
        try
        {
            TdlibVersion.Text = "TDLib " + TelegramService.GetNativeVersion();
            var saved = TelegramSettings.LoadConfigured();
            if (saved != null) StartTelegram(saved);
        }
        catch (Exception error) { TelegramStatus.Text = error.Message; }
    }
    private void ConnectTelegram_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ApiIdInput.Text.Trim(), out var id) || !TelegramSettings.IsValid(new TelegramCredentials(id, ApiHashInput.Password.Trim())))
        {
            TelegramStatus.Text = "Укажи API ID и API Hash своего приложения с my.telegram.org.";
            return;
        }
        try
        {
            var credentials = new TelegramCredentials(id, ApiHashInput.Password.Trim());
            if (_authMode is "phoneSetup" or "phone" && !string.IsNullOrWhiteSpace(AuthInput.Text))
                _pendingPhoneNumber = TelegramPhoneNumber.Normalize(AuthInput.Text);
            TelegramSettings.Save(credentials);
            _telegram?.Dispose();
            _telegram = null;
            StartTelegram(credentials);
            ApiHashInput.Clear();
        }
        catch (Exception error) { TelegramStatus.Text = error.Message; }
    }
    private void StartTelegram(TelegramCredentials credentials)
    {
        if (_telegram != null) return;
        _telegram = new TelegramService(credentials.ApiId, credentials.ApiHash);
        _telegram.StateChanged += (mode, message) => Ui(() => UpdateTelegramState(mode, message));
        _telegram.MessageReceived += (chatId, message) => Ui(() => OnTelegramMessage(chatId, message));
        _telegram.ChatPhotoChanged += (chatId, photo) => Ui(() => OnChatPhotoChanged(chatId, photo));
        _telegram.MessageContentChanged += (chatId, id, content) => Ui(() => OnMessageContentChanged(chatId, id, content));
        DeveloperSetupExpander.IsExpanded = false;
        if (_pendingPhoneNumber == null) AuthInput.Clear();
        _authMode = "connecting";
        AuthSubmit.IsEnabled = false;
        TelegramStatus.Text = "Подключение к Telegram…";
        _telegram.Start();
    }
    private void OpenTelegramApi_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://my.telegram.org/apps") { UseShellExecute = true });
    private void UpdateTelegramState(string mode, string message)
    {
        TelegramStatus.Text = message;
        if (mode == "error") { AuthSubmit.IsEnabled = true; return; }
        if (mode == "loading") { AuthSubmit.IsEnabled = false; return; }
        var previousMode = _authMode;
        _authMode = mode;
        AuthTitle.Text = mode switch { "code" => "Код подтверждения", "password" => "Пароль Telegram", "email" => "Электронная почта", "emailCode" => "Код из почты", _ => "Вход в Telegram" };
        AuthFieldLabel.Text = mode switch { "code" => "Код", "password" => "Пароль двухэтапной проверки", "email" => "Email", "emailCode" => "Код из письма", _ => "Номер телефона" };
        AuthSubmit.IsEnabled = true;
        AuthInput.Visibility = mode is "phone" or "code" or "email" or "emailCode" ? Visibility.Visible : Visibility.Collapsed;
        AuthPassword.Visibility = mode == "password" ? Visibility.Visible : Visibility.Collapsed;
        AuthSubmit.Visibility = mode is "phone" or "code" or "password" or "email" or "emailCode" ? Visibility.Visible : Visibility.Collapsed;
        if (previousMode != mode && mode != "phone") AuthInput.Clear();
        if (mode == "ready")
        {
            AuthPanel.Visibility = Visibility.Collapsed;
            EmptyChatHint.Visibility = Visibility.Visible;
            _ = LoadChatsAsync();
            _ = InitializeAccountLibraryAsync();
        }
        else if (mode is "phone" or "code" or "password" or "email" or "emailCode")
        {
            AuthPanel.Visibility = Visibility.Visible;
            if (mode == "password") AuthPassword.Focus(); else AuthInput.Focus();
            if (mode == "phone" && _pendingPhoneNumber != null)
            {
                var phone = _pendingPhoneNumber;
                _pendingPhoneNumber = null;
                AuthInput.Text = phone;
                _telegram?.SubmitPhone(phone);
                AuthSubmit.IsEnabled = false;
                TelegramStatus.Text = "Запрашиваем код подтверждения…";
            }
        }
    }
    private void AuthSubmit_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AuthInput.Text) && _authMode != "password") return;
        if (_authMode is "phoneSetup" or "phone")
        {
            try
            {
                var phone = TelegramPhoneNumber.Normalize(AuthInput.Text);
                AuthInput.Text = phone;
                if (_telegram == null)
                {
                    _pendingPhoneNumber = phone;
                    TelegramStatus.Text = "Для этой сборки ещё не настроены API ID и API Hash приложения. Укажи их один раз в настройке ниже; затем вход продолжится по номеру.";
                    DeveloperSetupExpander.IsExpanded = true;
                    return;
                }
                _telegram.SubmitPhone(phone);
                AuthSubmit.IsEnabled = false;
                TelegramStatus.Text = "Запрашиваем код подтверждения…";
            }
            catch (ArgumentException error) { TelegramStatus.Text = error.Message; }
            return;
        }
        switch (_authMode)
        {
            case "code": _telegram?.SubmitCode(AuthInput.Text.Trim()); break;
            case "password": _telegram?.SubmitPassword(AuthPassword.Password); AuthPassword.Clear(); break;
            case "email": _telegram?.SubmitEmail(AuthInput.Text.Trim()); break;
            case "emailCode": _telegram?.SubmitEmailCode(AuthInput.Text.Trim()); break;
        }
    }
    private void AuthField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return || !AuthSubmit.IsEnabled) return;
        e.Handled = true;
        AuthSubmit_Click(sender, new RoutedEventArgs());
    }
    private async Task LoadChatsAsync()
    {
        if (_telegram == null) return;
        ChatListStatus.Text = "Загружаем чаты…";
        try
        {
            var chats = await _telegram.GetChatsAsync();
            _chats.Clear();
            foreach (var chat in chats)
            {
                _chats.Add(chat);
                _ = _chatAvatars.LoadAsync(chat.Avatar, chat.Photo, _shutdown.Token);
            }
            ChatListStatus.Text = chats.Count == 0 ? "Пока нет чатов" : $"Загружено чатов: {chats.Count}";
        }
        catch (Exception error) { ChatListStatus.Text = error.Message; }
    }
    private async void ChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingChats || ChatList.SelectedItem is not TelegramChat chat || _telegram == null) return;
        if (_selectedChat != null && _selectedChat.Id != chat.Id) _telegram.CloseChat(_selectedChat.Id);
        _selectedChat = chat;
        HeaderAvatarVisual.Visibility = Visibility.Visible;
        ChatTitle.Text = chat.Title;
        ChatSubtitle.Text = "Загрузка сообщений…";
        AuthPanel.Visibility = EmptyChatHint.Visibility = Visibility.Collapsed;
        MessagesList.Visibility = ComposerBar.Visibility = Visibility.Visible;
        _messages.Clear();
        _hasOlderHistory = true;
        await LoadHistoryAsync(chat.Id);
    }
    private async Task LoadHistoryAsync(long chatId)
    {
        if (_telegram == null) return;
        _loadingHistory = true;
        try
        {
            var history = await _telegram.GetHistoryAsync(chatId);
            if (_selectedChat?.Id != chatId) return;
            _messages.Clear();
            string? date = null;
            foreach (var message in history) { message.ShowDate = date != message.Date; date = message.Date; AddMessage(message); }
            ChatSubtitle.Text = "Telegram";
            ScrollMessages();
        }
        catch (Exception error) { ChatSubtitle.Text = error.Message; }
        finally { _loadingHistory = false; }
    }
    private void OnTelegramMessage(long chatId, TelegramMessage message)
    {
        if (_selectedChat?.Id == chatId && !_messages.Any(m => m.Id == message.Id))
        { message.ShowDate = _messages.LastOrDefault()?.Date != message.Date; AddMessage(message); ScrollMessages(); }
        var chat = _chats.FirstOrDefault(c => c.Id == chatId);
        if (chat == null) return;
        var updated = chat with { Preview = message.Preview.Replace('\r', ' ').Replace('\n', ' '), Time = message.Time };
        _updatingChats = true;
        _chats[_chats.IndexOf(chat)] = updated;
        if (_selectedChat?.Id == chatId) { _selectedChat = updated; ChatList.SelectedItem = updated; }
        _updatingChats = false;
    }
    private void OnChatPhotoChanged(long chatId, TelegramChatPhoto? photo)
    {
        var chat = _chats.FirstOrDefault(item => item.Id == chatId);
        if (chat == null) return;
        var updated = chat with { Photo = photo };
        _updatingChats = true;
        _chats[_chats.IndexOf(chat)] = updated;
        if (_selectedChat?.Id == chatId) { _selectedChat = updated; ChatList.SelectedItem = updated; }
        _updatingChats = false;
        _ = _chatAvatars.LoadAsync(updated.Avatar, photo, _shutdown.Token);
    }
    private void ScrollMessages() { if (_messages.Count > 0) MessagesList.ScrollIntoView(_messages[^1]); }
    private async void SendMessage_Click(object sender, RoutedEventArgs e) => await SendMessageAsync();
    private async void MessageInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        await SendMessageAsync();
    }
    private async Task SendMessageAsync()
    {
        var chat = _selectedChat;
        var text = MessageInput.Text.Trim();
        if (_telegram == null || chat == null || text.Length == 0) return;
        try
        {
            await _telegram.SendTextAsync(chat.Id, text);
            MessageInput.Clear();
            await LoadHistoryAsync(chat.Id);
        }
        catch (Exception error) { ChatSubtitle.Text = error.Message; }
    }

    private void MusicSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (MusicSearchHint != null) MusicSearchHint.Visibility = MusicSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void MusicSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return) return;
        e.Handled = true;
        await SearchTracksAsync();
    }
    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchTracksAsync();
    private async Task SearchTracksAsync()
    {
        var query = MusicSearchBox.Text.Trim();
        if (query.Length == 0) return;
        _searchCancellation?.Cancel();
        _searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var cancellation = _searchCancellation;
        SearchButton.Content = "Поиск…";
        MusicStatus.Text = "Ищем в " + (_source == "Все" ? "YouTube и SoundCloud…" : _source + "…");
        _tracks.Clear();
        _showingPlaylist = false;
        _searchResults.Clear();
        RefreshTracks();
        try
        {
            var result = await _search.SearchAsync(query, _source, cancellation.Token, batch => Ui(() =>
            {
                if (cancellation.IsCancellationRequested) return;
                AddTracks(batch);
                MusicStatus.Text = "Уже можно включить трек. Проверяем остальные источники…";
            }));
            if (cancellation.IsCancellationRequested) return;
            AddTracks(result.Tracks);
            MusicStatus.Text = result.Tracks.Count > 0
                ? "Нажми ▶ рядом с треком или дважды щёлкни по нему. " + string.Join(" · ", result.Messages)
                : "Ничего не найдено. " + string.Join(" · ", result.Messages);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { MusicStatus.Text = error.Message; }
        finally { if (_searchCancellation == cancellation) { SearchButton.Content = "Найти"; _searchCancellation = null; } cancellation.Dispose(); }
    }
    private void AddTracks(IEnumerable<OnlineTrack> tracks)
    {
        foreach (var track in tracks)
            if (!_searchResults.Any(existing => existing.Url == track.Url))
            { _searchResults.Add(track); if (!_showingPlaylist) _tracks.Add(track); _ = LoadArtworkAsync(track); }
        RefreshTracks();
        PrepareVisibleTracks();
    }
    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button selected) return;
        _source = selected.Tag?.ToString() ?? "Все";
        foreach (var button in new[] { AllSources, YouTubeSource, SoundCloudSource })
            button.Background = Brush(button == selected ? "#3F769D" : "#223041");
        RefreshTracks();
        PrepareVisibleTracks();
    }
    private async Task LoadArtworkAsync(OnlineTrack track)
    {
        if (string.IsNullOrWhiteSpace(track.Thumbnail)) return;
        try
        {
            var bytes = await _artworkHttp.GetByteArrayAsync(track.Thumbnail, _shutdown.Token).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 160;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            Ui(() => track.Artwork = bitmap);
        }
        catch (Exception) { }
    }
    private void RefreshTracks()
    {
        _trackView?.Refresh();
        var count = _tracks.Count(track => _source == "Все" || track.Provider == _source);
        ResultCount.Text = count == 0 ? "" : $"Найдено: {count}";
        MusicEmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Добавить музыку", Filter = "Аудио|*.mp3;*.wav;*.m4a;*.ogg;*.opus;*.flac;*.aac|Все файлы|*.*", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        _source = "Все";
        foreach (var path in dialog.FileNames) _tracks.Add(new OnlineTrack(Path.GetFileNameWithoutExtension(path), "Мои файлы", "Локально", new Uri(path).AbsoluteUri, 0, IsLocal: true));
        RefreshTracks();
    }
    private async void TrackList_DoubleClick(object sender, MouseButtonEventArgs e)
    { if (TrackList.SelectedItem is OnlineTrack track) await PlayTrackAsync(track); }
    private async void TrackList_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Return && TrackList.SelectedItem is OnlineTrack track) { e.Handled = true; await PlayTrackAsync(track); } }
    private async void PlayTrack_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: OnlineTrack track }) await PlayTrackAsync(track); }
    private async void TrackList_Click(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        for (var ancestor = element; ancestor != null && ancestor != TrackList;)
        {
            if (ancestor is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.ScrollBar) return;
            ancestor = ancestor is Visual ? VisualTreeHelper.GetParent(ancestor) : LogicalTreeHelper.GetParent(ancestor);
        }
        if (ItemsControl.ContainerFromElement(TrackList, element) is ListBoxItem { DataContext: OnlineTrack track })
        {
            if (_playback?.Current is { State: TrackPlaybackState.Playing or TrackPlaybackState.Loading } playing && playing.Track.Url == track.Url) return;
            await PlayTrackAsync(track);
        }
    }
    private async Task PlayTrackAsync(OnlineTrack track, bool replaceQueue = true)
    {
        if (_playback == null) { MusicStatus.Text = "Аудиодвижок не загружен."; return; }
        if (replaceQueue)
        {
            _playQueue = _tracks.Where(t => _source == "Все" || t.Provider == _source).ToArray();
            if (!_playQueue.Any(t => t.Url == track.Url)) _playQueue = [track];
        }
        if (_warmupTarget != track.Url) _warmupCancellation?.Cancel();
        _currentTrack = track;
        await _playback.SelectAsync(track);
    }
    private void ApplyPlaybackSnapshot(PlaybackSnapshot snapshot)
    {
        if (_playback?.Current?.RequestId != snapshot.RequestId) return;
        _currentTrack = snapshot.Track;
        PlayerBar.Visibility = Visibility.Visible;
        NowPlaying.Text = snapshot.Track.Title;
        NowPlayingArtist.Text = snapshot.Track.Artist;
        PlayerArtwork.SetBinding(Image.SourceProperty, new Binding(nameof(OnlineTrack.Artwork)) { Source = snapshot.Track });
        PlayPauseButton.IsEnabled = snapshot.State != TrackPlaybackState.Loading;
        switch (snapshot.State)
        {
            case TrackPlaybackState.Loading:
                PlaybackStatus.Text = "Подключаем " + snapshot.Track.Provider + "…";
                PlayPauseButton.Content = "…";
                SeekSlider.Value = 0;
                ElapsedTime.Text = "0:00";
                TotalTime.Text = snapshot.Track.DurationText;
                break;
            case TrackPlaybackState.Playing:
                PlaybackStatus.Text = snapshot.Track.Provider;
                PlayPauseButton.Content = "Ⅱ";
                PrepareNextTrack(snapshot.Track);
                break;
            case TrackPlaybackState.Paused:
                PlaybackStatus.Text = "Пауза";
                PlayPauseButton.Content = "▶";
                break;
            case TrackPlaybackState.Error:
                PlaybackStatus.Text = "Не удалось включить";
                MusicStatus.Text = snapshot.Error;
                PlayPauseButton.Content = "▶";
                break;
            case TrackPlaybackState.Ended:
                PlayPauseButton.Content = "▶";
                _ = PlayNextAsync(1);
                break;
        }
    }
    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        var current = _playback?.Current;
        if (current == null) return;
        if (current.State is TrackPlaybackState.Error or TrackPlaybackState.Ended) { await PlayTrackAsync(current.Track); return; }
        _playback?.SetPaused(current.State == TrackPlaybackState.Playing);
    }
    private void PrepareNextTrack(OnlineTrack? after)
    {
        var visible = _playQueue.ToList();
        if (visible.Count == 0) return;
        var index = after == null ? -1 : visible.FindIndex(t => t.Url == after.Url);
        if (visible.Count == 1 && index == 0) return;
        var next = visible[(index + 1) % visible.Count];
        if (next.IsLocal || (_warmupTarget == next.Url && _warmupCancellation != null)) return;
        _warmupCancellation?.Cancel();
        _warmupCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _warmupTarget = next.Url;
        _ = WarmStreamAsync(next, _warmupCancellation);
    }
    private async Task WarmStreamAsync(OnlineTrack track, CancellationTokenSource cancellation)
    {
        try { var stream = await _streams.ResolveAudioAsync(track, cancellation.Token); if (_audio != null) await _audio.PrepareAsync(stream); }
        catch (Exception) { }
        finally
        {
            if (_warmupCancellation == cancellation) { _warmupCancellation = null; _warmupTarget = null; }
            cancellation.Dispose();
        }
    }
    private async void Previous_Click(object sender, RoutedEventArgs e) => await PlayNextAsync(-1);
    private async void Next_Click(object sender, RoutedEventArgs e) => await PlayNextAsync(1);
    private async Task PlayNextAsync(int direction)
    {
        var tracks = _playQueue.ToList();
        if (tracks.Count == 0) return;
        var index = _currentTrack == null ? -1 : tracks.FindIndex(t => t.Url == _currentTrack.Url);
        var next = (index + direction + tracks.Count) % tracks.Count;
        TrackList.SelectedItem = tracks[next];
        await PlayTrackAsync(tracks[next], replaceQueue: false);
    }
    private void UpdatePlaybackPosition()
    {
        if (_audio == null || _playback?.Current?.State is not (TrackPlaybackState.Playing or TrackPlaybackState.Paused)) return;
        var time = Math.Max(0, _audio.Time);
        var length = Math.Max(0, _audio.Length);
        ElapsedTime.Text = FormatTime(time);
        if (length > 0) { TotalTime.Text = FormatTime(length); if (!_seeking) SeekSlider.Value = Math.Clamp((double)time / length, 0, 1); }
    }
    private static string FormatTime(long milliseconds) => TimeSpan.FromMilliseconds(milliseconds).ToString(milliseconds >= 3600000 ? @"h\:mm\:ss" : @"m\:ss");
    private void Seek_Down(object sender, MouseButtonEventArgs e) => _seeking = true;
    private void Seek_Up(object sender, MouseButtonEventArgs e) { Seek(); _seeking = false; }
    private void Seek_KeyUp(object sender, KeyEventArgs e) => Seek();
    private void Seek() { if (_audio != null && _audio.IsSeekable) _audio.Position = (float)SeekSlider.Value; }
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_audio != null) _audio.Volume = (int)e.NewValue; }
    private void Ui(Action action) { if (!_closing && !Dispatcher.HasShutdownStarted) Dispatcher.InvokeAsync(action); }
    private void OnClosed(object? sender, EventArgs e)
    {
        _tray?.Dispose();
        StopLibraryTimer();
        _preparation.Dispose();
        _closing = true;
        _playbackTimer.Stop();
        _shutdown.Cancel();
        _searchCancellation?.Cancel();
        _warmupCancellation?.Cancel();
        _telegram?.Dispose();
        if (_playback != null) _playback.Dispose(); else _audio?.Dispose();
        _streams.Dispose();
        _search.Dispose();
        _artworkHttp.Dispose();
        _shutdown.Dispose();
    }
}
