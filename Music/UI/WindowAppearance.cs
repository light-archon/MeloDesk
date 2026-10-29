using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MeloDesk.UI;

public static class WindowAppearance
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    public static void UseDarkTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var enabled = 1; DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        var color = 0x002B2117; DwmSetWindowAttribute(handle, 35, ref color, sizeof(int));
    }
}
