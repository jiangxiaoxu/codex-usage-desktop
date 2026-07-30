using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodexUsage.App.Platform;

internal sealed class ShellTrayIcon : IDisposable
{
    private const int GwlWndProc = -4;
    private const uint WmNull = 0x0000;
    private const uint WmContextMenu = 0x007B;
    private const uint WmDpiChanged = 0x02E0;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint TrayCallbackMessage = 0x8000 + 42;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmNonotify = 0x0080;
    private const uint TpmReturnCommand = 0x0100;
    private const uint ShowCommand = 1;
    private const uint ExitCommand = 2;
    private const int IdiApplication = 32512;

    private readonly IntPtr _windowHandle;
    private readonly Action _showWindow;
    private readonly Action _requestExit;
    private readonly Action _dpiChanged;
    private readonly WindowProcedure _windowProcedure;
    private readonly IntPtr _previousWindowProcedure;
    private NotifyIconData _iconData;
    private int _disposed;

    public ShellTrayIcon(
        IntPtr windowHandle,
        Action showWindow,
        Action requestExit,
        Action dpiChanged)
    {
        _windowHandle = windowHandle;
        _showWindow = showWindow;
        _requestExit = requestExit;
        _dpiChanged = dpiChanged;
        _windowProcedure = WindowProc;
        _previousWindowProcedure = SetWindowLongPtr(
            windowHandle,
            GwlWndProc,
            Marshal.GetFunctionPointerForDelegate(_windowProcedure));
        if (_previousWindowProcedure == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法安装 tray window procedure");
        }

        _iconData = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = windowHandle,
            Id = 1,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = TrayCallbackMessage,
            IconHandle = LoadIcon(IntPtr.Zero, new IntPtr(IdiApplication)),
            ToolTip = "Codex Usage Desktop",
            Info = string.Empty,
            InfoTitle = string.Empty,
        };

        if (!ShellNotifyIcon(NimAdd, ref _iconData))
        {
            _ = SetWindowLongPtr(windowHandle, GwlWndProc, _previousWindowProcedure);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建系统托盘图标");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ = ShellNotifyIcon(NimDelete, ref _iconData);
        _ = SetWindowLongPtr(_windowHandle, GwlWndProc, _previousWindowProcedure);
        GC.KeepAlive(_windowProcedure);
    }

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmDpiChanged)
        {
            _dpiChanged();
        }
        else if (message == TrayCallbackMessage)
        {
            var notification = unchecked((uint)lParam.ToInt64());
            if (notification is WmLButtonUp or WmLButtonDoubleClick)
            {
                _showWindow();
                return IntPtr.Zero;
            }

            if (notification is WmRButtonUp or WmContextMenu)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }
        }

        return CallWindowProc(_previousWindowProcedure, window, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = AppendMenu(menu, MfString, ShowCommand, "显示 Codex Usage Desktop");
            _ = AppendMenu(menu, MfSeparator, 0, string.Empty);
            _ = AppendMenu(menu, MfString, ExitCommand, "退出");
            _ = SetForegroundWindow(_windowHandle);
            _ = GetCursorPos(out var cursor);
            var command = TrackPopupMenuEx(
                menu,
                TpmRightButton | TpmNonotify | TpmReturnCommand,
                cursor.X,
                cursor.Y,
                _windowHandle,
                IntPtr.Zero);

            if (command == ShowCommand)
            {
                _showWindow();
            }
            else if (command == ExitCommand)
            {
                _requestExit();
            }

            _ = PostMessage(_windowHandle, WmNull, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ToolTip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIconHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint item, string text);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        IntPtr window,
        IntPtr parameters);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
