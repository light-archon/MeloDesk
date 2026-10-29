using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MeloDesk.Telegram;

namespace MeloDesk.UI;

public sealed class MessageAttachmentView : StackPanel
{
    public static readonly DependencyProperty AttachmentProperty = DependencyProperty.Register(nameof(Attachment), typeof(TelegramAttachment), typeof(MessageAttachmentView), new PropertyMetadata(null, (d, _) => ((MessageAttachmentView)d).Build()));
    public TelegramAttachment? Attachment { get => (TelegramAttachment?)GetValue(AttachmentProperty); set => SetValue(AttachmentProperty, value); }
    private CancellationTokenSource? _lifetime;
    private Image? _image;
    private TgsStickerImage? _sticker;
    private TextBlock? _status;
    private Button? _open;
    private ProgressBar? _progress;
    private TelegramService? _telegram;
    private bool _busy;
    public MessageAttachmentView()
    {
        Loaded += (_, _) => { _lifetime = new(); _ = LoadPreviewAsync(); };
        Unloaded += (_, _) => { _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null; if (_telegram != null) _telegram.FileProgress -= OnFileProgress; _telegram = null; };
    }
    private void Build()
    {
        _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = IsLoaded ? new() : null;
        if (_telegram != null) _telegram.FileProgress -= OnFileProgress;
        _telegram = null; _busy = false; _image = null; _sticker = null;
        Children.Clear();
        if (Attachment is not { } media) return;
        MaxWidth = 420;
        if (media.IsVisual)
        {
            _image = new Image { MaxHeight = 340, MaxWidth = 420, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
            if (media.Kind == "VideoNote") { _image.Width = _image.Height = 220; _image.Stretch = Stretch.UniformToFill; _image.Clip = new EllipseGeometry(new Point(110, 110), 110, 110); }
            Children.Add(_image);
            if (media.Kind == "Sticker" && media.Format == "stickerFormatTgs") { _sticker = new TgsStickerImage { Visibility = Visibility.Collapsed }; Children.Add(_sticker); }
        }
        var row = new DockPanel { Margin = new Thickness(0, 7, 0, 3) };
        _open = new Button { Content = media.Kind == "Photo" ? "Открыть фото" : media.IsPlayable ? "▶" : "↓", Padding = new Thickness(12, 7, 12, 7), MinWidth = 40, VerticalAlignment = VerticalAlignment.Center };
        _open.Click += async (_, _) => await OpenAsync(saveOnly: !media.IsPlayable && media.Kind != "Photo" && media.Kind != "Sticker");
        DockPanel.SetDock(_open, Dock.Left); row.Children.Add(_open);
        var save = new Button { Content = "↓", ToolTip = "Сохранить файл как…", Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        save.Click += async (_, _) => await OpenAsync(saveOnly: true);
        DockPanel.SetDock(save, Dock.Right); row.Children.Add(save);
        var details = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(new TextBlock { Text = media.Kind == "Document" || media.Kind == "Audio" ? media.Name : media.Label, Foreground = new SolidColorBrush(Color.FromRgb(229, 237, 245)), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 275, FontSize = 13 });
        if (media.Kind == "Voice") details.Children.Add(new VoiceWaveform(media.Waveform) { Width = 180, Height = 25, Margin = new Thickness(0, 3, 0, 0) });
        _status = new TextBlock { Text = (media.Duration > 0 ? media.DurationText + " · " : "") + media.SizeText, Foreground = new SolidColorBrush(Color.FromRgb(135, 169, 193)), FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
        details.Children.Add(_status); row.Children.Add(details); Children.Add(row);
        _progress = new ProgressBar { Height = 3, Maximum = 100, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 3) };
        Children.Add(_progress);
        if (IsLoaded) _ = LoadPreviewAsync();
    }
    private async Task LoadPreviewAsync()
    {
        if (Attachment is not { IsVisual: true } media || _image == null || Window.GetWindow(this) is not MainWindow owner || owner.Telegram == null) return;
        var cancellation = _lifetime?.Token ?? CancellationToken.None;
        try
        {
            if (media.Kind == "Sticker" && media.Format == "stickerFormatTgs")
            {
                var stickerPath = media.LocalPath ?? await owner.Telegram.GetFileAsync(media.FileId, cancellation);
                if (stickerPath != null && !cancellation.IsCancellationRequested && ReferenceEquals(Attachment, media) && _sticker != null) { _sticker.Load(stickerPath); _sticker.Visibility = Visibility.Visible; _image.Visibility = Visibility.Collapsed; }
                return;
            }
            if (media.MiniPreview is { Length: > 0 }) { try { _image.Source = Decode(media.MiniPreview); } catch (Exception) { } }
            var path = media.ThumbnailPath;
            if (path == null && media.ThumbnailId > 0) path = await owner.Telegram.GetFileAsync(media.ThumbnailId, cancellation);
            if (path == null && media.Kind == "Photo" && media.Size <= 20_000_000) path = media.LocalPath ?? await owner.Telegram.GetFileAsync(media.FileId, cancellation);
            if (media.Kind == "Sticker" && media.Format == "stickerFormatWebp") path = media.LocalPath ?? await owner.Telegram.GetFileAsync(media.FileId, cancellation);
            if (path != null)
            {
                var bytes = await File.ReadAllBytesAsync(path, cancellation);
                var image = await Task.Run(() => Decode(bytes), cancellation);
                if (!cancellation.IsCancellationRequested && ReferenceEquals(Attachment, media)) _image.Source = image;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (_status != null && ReferenceEquals(Attachment, media)) _status.Text = "Нажми, чтобы загрузить · " + media.SizeText; }
    }
    private void OnFileProgress(TelegramFileProgress progress)
    {
        if (progress.FileId != Attachment?.FileId || !_busy) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (_progress == null || _status == null) return;
            _progress.IsIndeterminate = progress.Total <= 0;
            _progress.Value = progress.Total > 0 ? progress.Downloaded * 100d / progress.Total : 0;
            _status.Text = progress.Total > 0 ? $"Загрузка {_progress.Value:0}%" : "Загружаем файл…";
        });
    }
    private async Task OpenAsync(bool saveOnly)
    {
        if (_busy || Attachment is not { } media || Window.GetWindow(this) is not MainWindow owner || owner.Telegram is not { } telegram) return;
        string? destination = null;
        if (saveOnly)
        {
            var dialog = new SaveFileDialog { FileName = string.Join("_", media.Name.Split(Path.GetInvalidFileNameChars())), Filter = "Все файлы|*.*" };
            if (dialog.ShowDialog(owner) != true) return;
            destination = dialog.FileName;
        }
        _busy = true; _telegram = telegram; _telegram.FileProgress += OnFileProgress;
        if (_open != null) _open.IsEnabled = false;
        if (_progress != null) { _progress.Visibility = Visibility.Visible; _progress.IsIndeterminate = true; }
        if (_status != null) _status.Text = "Загружаем файл…";
        try
        {
            var path = media.LocalPath is { } local && File.Exists(local) ? local : await telegram.GetFileAsync(media.FileId, _lifetime?.Token ?? CancellationToken.None);
            if (path == null) throw new IOException("Файл пока недоступен в Telegram. Попробуй ещё раз.");
            if (!ReferenceEquals(Attachment, media) || !IsLoaded) return;
            if (saveOnly) { File.Copy(path, destination!, overwrite: true); if (_status != null) _status.Text = "Файл сохранён"; }
            else if (media.Kind == "Photo" || media.Kind == "Sticker" && media.Format == "stickerFormatWebp") new PhotoWindow(path) { Owner = owner, Resources = owner.Resources }.Show();
            else if (media.Kind == "Sticker" && media.Format == "stickerFormatTgs") { _sticker?.Load(path); }
            else { owner.PauseMusicForAttachment(); new AttachmentPlayerWindow(path, media) { Owner = owner, Resources = owner.Resources }.Show(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (_status != null && ReferenceEquals(Attachment, media)) _status.Text = error.Message; }
        finally
        {
            telegram.FileProgress -= OnFileProgress;
            if (ReferenceEquals(Attachment, media))
            {
                _busy = false;
                if (_open != null) _open.IsEnabled = true;
                if (_progress != null) _progress.Visibility = Visibility.Collapsed;
            }
        }
    }
    private static BitmapSource Decode(byte[] bytes)
    {
        return MediaImageDecoder.Decode(bytes);
    }
}

public sealed class VoiceWaveform(byte[]? data) : FrameworkElement
{
    protected override void OnRender(DrawingContext context)
    {
        var count = data == null ? 0 : data.Length * 8 / 5;
        var bars = Math.Max(1, Math.Min(50, count));
        var brush = new SolidColorBrush(Color.FromRgb(103, 174, 216));
        for (var i = 0; i < bars; i++)
        {
            var bit = count == 0 ? 0 : i * count / bars * 5;
            var sample = count == 0 ? 3 : ((data![bit / 8] | (bit / 8 + 1 < data.Length ? data[bit / 8 + 1] << 8 : 0)) >> (bit % 8)) & 31;
            var height = Math.Max(3, ActualHeight * sample / 31d);
            context.DrawRoundedRectangle(brush, null, new Rect(i * ActualWidth / bars, (ActualHeight - height) / 2, Math.Max(2, ActualWidth / bars - 2), height), 1, 1);
        }
    }
}
