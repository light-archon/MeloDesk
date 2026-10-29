using System.Windows;

namespace MeloDesk;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _showWindow;
    private RegisteredWaitHandle? _showRegistration;
    public bool IsExiting { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _showWindow = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MeloDesk.ShowWindow");
        _instance = new Mutex(true, @"Local\MeloDesk.Client", out var created);
        if (!created) { _showWindow.Set(); Shutdown(); return; }
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(_showWindow, (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (!IsExiting && MainWindow is MainWindow window) window.RestoreFromTray();
        }), null, Timeout.Infinite, false);
        base.OnStartup(e);
    }
    public async void ExitApplication()
    {
        if (IsExiting) return;
        IsExiting = true;
        try { if (MainWindow is MainWindow window) await window.SaveSessionAndCloseAsync(); }
        finally { Shutdown(); }
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e) { IsExiting = true; base.OnSessionEnding(e); }
    protected override void OnExit(ExitEventArgs e)
    {
        _showRegistration?.Unregister(null);
        _showWindow?.Dispose(); _instance?.Dispose(); base.OnExit(e);
    }
}
