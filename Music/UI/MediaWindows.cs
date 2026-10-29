using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using MeloDesk.Telegram;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace MeloDesk.UI;

public sealed class PhotoWindow : Window
{
    public PhotoWindow(string path)
    {
        Title = "Фотография · MeloDesk"; Width = 950; Height = 760; Background = Brushes.Black; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var bitmap = MediaImageDecoder.Decode(File.ReadAllBytes(path), 2400);
        var image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        Content = new ScrollViewer { Content = image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        Loaded += (_, _) => WindowAppearance.UseDarkTitleBar(this);
    }
}

public sealed class AttachmentPlayerWindow : Window
{
    private readonly LibVLC _engine;
    private readonly MediaPlayer _player;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _started;
    public AttachmentPlayerWindow(string path, TelegramAttachment attachment)
    {
        Title = attachment.Label + " · MeloDesk"; Width = attachment.IsVisual ? 850 : 540; Height = attachment.IsVisual ? 640 : 250;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(14, 22, 33)); Foreground = Brushes.White;
        Core.Initialize(); _engine = new LibVLC("--no-osd", "--no-media-library"); _player = new MediaPlayer(_engine);
        var root = new DockPanel();
        var bar = new StackPanel { Margin = new Thickness(18) }; DockPanel.SetDock(bar, Dock.Bottom);
        var status = new TextBlock { Text = attachment.Name, Margin = new Thickness(0, 0, 0, 12), TextTrimming = TextTrimming.CharacterEllipsis };
        var seek = new Slider { Minimum = 0, Maximum = 1 }; var seeking = false;
        seek.PreviewMouseLeftButtonDown += (_, _) => seeking = true;
        seek.PreviewMouseLeftButtonUp += (_, _) => { if (_player.IsSeekable) _player.Position = (float)seek.Value; seeking = false; };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var pause = new Button { Content = "Ⅱ / ▶", Padding = new Thickness(20, 7, 20, 7), Margin = new Thickness(10) };
        pause.Click += (_, _) => _player.SetPause(_player.IsPlaying);
        var volume = new Slider { Width = 120, Minimum = 0, Maximum = 100, Value = 70, VerticalAlignment = VerticalAlignment.Center };
        volume.ValueChanged += (_, _) => _player.Volume = (int)volume.Value;
        controls.Children.Add(pause); controls.Children.Add(volume); bar.Children.Add(status); bar.Children.Add(seek); bar.Children.Add(controls); root.Children.Add(bar);
        if (attachment.IsVisual) root.Children.Add(new VideoView { MediaPlayer = _player });
        else root.Children.Add(new TextBlock { Text = attachment.Kind == "Voice" ? "Голосовое сообщение" : attachment.Name, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        Content = root;
        _timer.Tick += (_, _) => { if (!seeking) seek.Value = Math.Max(0, _player.Position); status.Text = FormatTime(_player.Time) + " / " + FormatTime(_player.Length); };
        _player.EncounteredError += (_, _) => Dispatcher.InvokeAsync(() => status.Text = "Не удалось воспроизвести этот формат. Можно сохранить оригинальный файл.");
        Loaded += (_, _) => { WindowAppearance.UseDarkTitleBar(this); if (_started) return; _started = true; using var media = new Media(_engine, new Uri(path)); _player.Play(media); _player.Volume = 70; _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); _player.Mute = true; _player.Stop(); _player.Dispose(); _engine.Dispose(); };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }
    private static string FormatTime(long milliseconds) => TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)).ToString(@"h\:mm\:ss");
}
