using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MeloDesk.Music;
using MeloDesk.Telegram;

namespace MeloDesk;

public partial class MainWindow
{
    private PlaylistLibrary? _library;
    private bool _libraryBusy;
    private bool _refreshingPlaylists;
    private bool _showingPlaylist;
    private readonly List<OnlineTrack> _searchResults = [];
    private readonly DispatcherTimer _libraryTimer = new() { Interval = TimeSpan.FromSeconds(45) };
    private readonly StreamPreparationQueue _preparation = new();
    private void InitializeLibraryTimer()
    {
        _libraryTimer.Tick += async (_, _) => { if (MusicView.Visibility == Visibility.Visible) await SyncLibraryAsync(true); };
        _libraryTimer.Start();
    }
    private void StopLibraryTimer() => _libraryTimer.Stop();
    private async Task InitializeAccountLibraryAsync()
    {
        if (_telegram == null) return;
        try
        {
            var accountId = await _telegram.GetAccountIdAsync();
            _library = new PlaylistLibrary(accountId, null, localOnly: true);
            _library.LoadLocal(); RefreshPlaylists();
            await SyncLibraryAsync(true);
        }
        catch (Exception error) { PlaylistStatus.Text = "Плейлисты: " + error.Message; }
    }
    private async Task SyncLibraryAsync(bool uploadPending)
    {
        if (_library == null || _libraryBusy) return;
        _libraryBusy = true;
        PlaylistStatus.Text = "Читаем локальную библиотеку…";
        try { await _library.SyncAsync(uploadPending); PlaylistStatus.Text = _library.Pending ? "Изменения сохранены на этом компьютере. Нажми ↻, чтобы отправить в Избранное." : "Сохранено на этом компьютере"; }
        catch (Exception error) { PlaylistStatus.Text = "Синхронизация недоступна: " + error.Message + (_library.Pending ? " Изменения сохранены локально; нажми ↻ для повтора." : ""); }
        finally { _libraryBusy = false; RefreshPlaylists(); }
    }
    private void RefreshPlaylists()
    {
        var id = (PlaylistSelector.SelectedItem as MusicPlaylist)?.Id;
        _refreshingPlaylists = true;
        var playlists = _library?.Playlists ?? [];
        PlaylistSelector.ItemsSource = playlists;
        PlaylistSelector.SelectedItem = playlists.FirstOrDefault(p => p.Id == id) ?? playlists.FirstOrDefault();
        _refreshingPlaylists = false;
        if (_showingPlaylist) DisplayPlaylist();
    }
    private async Task ChangePlaylistAsync(string kind, string id, string? name = null, OnlineTrack? track = null, string? trackUrl = null)
    {
        if (_library == null) { PlaylistStatus.Text = "Сначала войди в Telegram: плейлисты привязаны к аккаунту."; return; }
        if (_libraryBusy) { PlaylistStatus.Text = "Дождись завершения синхронизации."; return; }
        _libraryBusy = true;
        try { await _library.ChangeAsync(kind, id, name, track, trackUrl); PlaylistStatus.Text = "Сохранено на этом компьютере"; }
        catch (Exception error) { PlaylistStatus.Text = error.Message + (_library.Pending ? " Изменение сохранено локально; нажми ↻ для повтора." : ""); }
        finally { _libraryBusy = false; RefreshPlaylists(); }
    }
    private async void CreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PlaylistName.Text)) { PlaylistStatus.Text = "Введи название нового плейлиста в поле слева."; PlaylistName.Focus(); return; }
        var id = Guid.NewGuid().ToString();
        await ChangePlaylistAsync("create", id, PlaylistName.Text);
        PlaylistSelector.SelectedItem = _library?.Playlists.FirstOrDefault(p => p.Id == id);
        PlaylistName.Clear();
    }
    private async void RenamePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistSelector.SelectedItem is not MusicPlaylist p) return;
        if (string.IsNullOrWhiteSpace(PlaylistName.Text)) { PlaylistStatus.Text = "Введи новое название плейлиста в поле слева."; return; }
        await ChangePlaylistAsync("rename", p.Id, PlaylistName.Text); PlaylistName.Clear();
    }
    private async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistSelector.SelectedItem is MusicPlaylist p && MessageBox.Show(this, $"Удалить плейлист «{p.Name}» с этого компьютера?", "MeloDesk", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await ChangePlaylistAsync("delete", p.Id);
    }
    private async void AddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistSelector.SelectedItem is not MusicPlaylist p) { PlaylistStatus.Text = "Создай или выбери плейлист над списком треков."; return; }
        if (sender is Button { Tag: OnlineTrack track }) await ChangePlaylistAsync("add", p.Id, track: track);
    }
    private async void RemoveFromPlaylist_Click(object sender, RoutedEventArgs e)
    { if (PlaylistSelector.SelectedItem is MusicPlaylist p && sender is Button { Tag: OnlineTrack track }) await ChangePlaylistAsync("remove", p.Id, trackUrl: track.Url); }
    private async void SyncPlaylists_Click(object sender, RoutedEventArgs e) => await SyncLibraryAsync(true);
    private void Playlist_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_refreshingPlaylists) { _showingPlaylist = true; DisplayPlaylist(); } }
    private void DisplayPlaylist()
    {
        _tracks.Clear();
        if (PlaylistSelector.SelectedItem is MusicPlaylist p) { foreach (var track in p.Tracks) { _tracks.Add(track); _ = LoadArtworkAsync(track); } MusicStatus.Text = p.Name; }
        RefreshTracks(); PrepareVisibleTracks();
    }
    private void ShowSearchResults_Click(object sender, RoutedEventArgs e)
    { _showingPlaylist = false; _tracks.Clear(); foreach (var track in _searchResults) _tracks.Add(track); RefreshTracks(); PrepareVisibleTracks(); }
    private void TrackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (TrackList.SelectedItem is OnlineTrack track) PrepareTrack(track); }
    private string? _hoverTrack;
    private void TrackList_MouseMove(object sender, MouseEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(TrackList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item?.DataContext is OnlineTrack track && _hoverTrack != track.Url) { _hoverTrack = track.Url; PrepareTrack(track); }
    }
    private void PrepareTrack(OnlineTrack track) => _preparation.Prepare(track, async (item, token) =>
    {
        var stream = await _streams.ResolveAudioAsync(item, token);
        if (_audio != null) await _audio.PrepareAsync(stream);
        return stream;
    });
    private void PrepareVisibleTracks()
    { foreach (var track in _tracks.Where(t => _source == "Все" || t.Provider == _source).Take(2)) PrepareTrack(track); }

    private void AddMessage(TelegramMessage message)
    {
        message.SenderName = message.IsOutgoing ? "Вы" : message.Signature.Length > 0 ? message.Signature : "Участник";
        _messages.Add(message); _ = PopulateSenderAsync(message);
    }
    private async Task PopulateSenderAsync(TelegramMessage message)
    {
        if (_telegram == null) return;
        try
        {
            var sender = await _telegram.GetSenderAsync(message);
            Ui(() =>
            {
                message.SenderName = message.IsOutgoing ? "Вы" : sender.Name + (message.Signature.Length > 0 && sender.Name != message.Signature ? " · " + message.Signature : "");
                _ = _chatAvatars.LoadAsync(message.SenderAvatar, sender.Photo, _shutdown.Token);
            });
        }
        catch (Exception) { Ui(() => message.SenderName = message.IsOutgoing ? "Вы" : message.Signature.Length > 0 ? message.Signature : "Участник " + message.SenderId); }
    }
    private void OnMessageContentChanged(long chatId, long id, JsonElement content)
    {
        if (_selectedChat?.Id != chatId) return;
        var old = _messages.FirstOrDefault(m => m.Id == id);
        if (old == null) return;
        var field = TelegramMessageParser.S(content, "@type") == "messageText" ? "text" : "caption";
        var body = content.TryGetProperty(field, out var text) ? TelegramMessageParser.S(text, "text") : "";
        _messages[_messages.IndexOf(old)] = old with { Body = body, Attachment = TelegramMessageParser.ParseAttachment(content) };
    }
    private async void MessagesScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (_loadingHistory || !_hasOlderHistory || e.VerticalChange >= 0 || e.VerticalOffset > 1 || _messages.Count == 0 || _selectedChat == null || _telegram == null) return;
        _loadingHistory = true;
        var chatId = _selectedChat.Id; var anchor = _messages[0];
        try
        {
            var older = await _telegram.GetOlderHistoryAsync(chatId, anchor.Id);
            if (_selectedChat?.Id != chatId) return;
            var fresh = older.Where(m => !_messages.Any(old => old.Id == m.Id)).OrderBy(m => m.Id).ToArray();
            _hasOlderHistory = fresh.Length > 0;
            foreach (var message in fresh.Reverse()) { message.SenderName = "Участник"; _messages.Insert(0, message); _ = PopulateSenderAsync(message); }
            for (var i = 0; i < _messages.Count; i++) _messages[i].ShowDate = i == 0 || _messages[i - 1].Date != _messages[i].Date;
            MessagesList.ScrollIntoView(anchor);
        }
        catch (Exception error) { ChatSubtitle.Text = "История: " + error.Message; }
        finally { _loadingHistory = false; }
    }
}
