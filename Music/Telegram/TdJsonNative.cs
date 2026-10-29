using System.Runtime.InteropServices;

namespace MeloDesk.Telegram;

internal static class TdJsonNative
{
    [DllImport("tdjson", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int td_create_client_id();

    [DllImport("tdjson", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void td_send(int clientId, [MarshalAs(UnmanagedType.LPUTF8Str)] string request);

    [DllImport("tdjson", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr td_receive(double timeout);

    [DllImport("tdjson", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr td_execute([MarshalAs(UnmanagedType.LPUTF8Str)] string request);
}
