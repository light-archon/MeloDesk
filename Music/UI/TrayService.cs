using System.ComponentModel;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace MeloDesk.UI;

public sealed class TrayService : IDisposable
{
    private readonly Window _window;
    private readonly Func<bool> _isExiting;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Stream _iconStream;
    private readonly Drawing.Icon _icon;
    private readonly List<Window> _hiddenChildren = [];
    private WindowState _restoreState = WindowState.Normal;
    private bool _disposed;

    public TrayService(Window window, Func<bool> isExiting, Action exit)
    {
        _window = window; _isExiting = isExiting;
        _iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/MeloDesk.ico"))!.Stream;
        _icon = new Drawing.Icon(_iconStream);
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("Открыть MeloDesk", null, (_, _) => OnUi(Restore));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Выйти", null, (_, _) => OnUi(exit));
        _tray = new Forms.NotifyIcon { Text = "MeloDesk", Icon = _icon, ContextMenuStrip = _menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OnUi(Restore); };
        _window.Closing += OnClosing;
    }
    private void OnUi(Action action) => _window.Dispatcher.InvokeAsync(() => { if (!_disposed) action(); });
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_disposed || _isExiting()) return;
        e.Cancel = true;
        if (_window.WindowState != WindowState.Minimized) _restoreState = _window.WindowState;
        _hiddenChildren.Clear();
        foreach (Window child in _window.OwnedWindows)
            if (child.IsVisible) { _hiddenChildren.Add(child); child.Hide(); }
        _window.Hide();
    }
    public void Restore()
    {
        if (_disposed || _isExiting()) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = _restoreState;
        foreach (var child in _hiddenChildren) if (child.IsLoaded) child.Show();
        _hiddenChildren.Clear();
        _window.Activate();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _window.Closing -= OnClosing;
        _tray.Visible = false; _tray.Dispose(); _menu.Dispose(); _icon.Dispose(); _iconStream.Dispose();
    }
}
