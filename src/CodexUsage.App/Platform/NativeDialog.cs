using System.Runtime.InteropServices;

namespace CodexUsage.App.Platform;

internal static class NativeDialog
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    private const uint MbSetForeground = 0x00010000;

    public static void ShowError(string title, string message)
    {
        _ = MessageBox(IntPtr.Zero, message, title, MbOk | MbIconError | MbSetForeground);
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
