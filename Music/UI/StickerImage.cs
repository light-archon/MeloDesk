using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;
using SkiaSharp.Skottie;

namespace MeloDesk.UI;

public static class MediaImageDecoder
{
    public static BitmapSource Decode(byte[] bytes, int width = 640)
    {
        using var decoded = SKBitmap.Decode(bytes) ?? throw new IOException("Формат изображения не поддерживается.");
        using var data = decoded.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = Math.Min(width, decoded.Width);
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
}

public sealed class TgsStickerImage : Image
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Stopwatch _clock = new();
    private Animation? _animation;
    private SKSurface? _surface;
    private WriteableBitmap? _bitmap;
    public TgsStickerImage()
    {
        Width = Height = 220; Stretch = Stretch.Uniform;
        _timer.Tick += (_, _) => RenderFrame();
        Unloaded += (_, _) => { _timer.Stop(); _animation?.Dispose(); _animation = null; _surface?.Dispose(); _surface = null; };
        IsVisibleChanged += (_, _) => { if (IsVisible && _animation != null) _timer.Start(); else _timer.Stop(); };
    }
    public void Load(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = gzip.Read(buffer)) != 0) { output.Write(buffer, 0, read); if (output.Length > 4_000_000) throw new IOException("Стикер слишком большой."); }
        _animation?.Dispose();
        _animation = Animation.Parse(System.Text.Encoding.UTF8.GetString(output.ToArray())) ?? throw new IOException("Не удалось открыть анимированный стикер.");
        _surface?.Dispose(); _surface = SKSurface.Create(new SKImageInfo(256, 256, SKColorType.Bgra8888, SKAlphaType.Premul));
        _bitmap = new WriteableBitmap(256, 256, 96, 96, PixelFormats.Pbgra32, null); Source = _bitmap;
        _clock.Restart(); RenderFrame(); _timer.Start();
    }
    private void RenderFrame()
    {
        if (_animation == null || _surface == null || _bitmap == null) return;
        var duration = Math.Max(.01, _animation.Duration.TotalSeconds);
        _animation.Seek((_clock.Elapsed.TotalSeconds % duration) / duration);
        _surface.Canvas.Clear(SKColors.Transparent);
        _animation.Render(_surface.Canvas, new SKRect(0, 0, 256, 256));
        using var pixels = _surface.PeekPixels();
        _bitmap.WritePixels(new Int32Rect(0, 0, 256, 256), pixels.GetPixels(), pixels.RowBytes * 256, pixels.RowBytes);
    }
}
