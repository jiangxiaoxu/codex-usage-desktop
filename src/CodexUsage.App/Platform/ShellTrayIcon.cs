using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CodexUsage.Application;

namespace CodexUsage.App.Platform;

internal sealed class ShellTrayIcon : IDisposable
{
    private const int GwlWndProc = -4;
    private const uint WmNull = 0x0000;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmContextMenu = 0x007B;
    private const uint WmNcLButtonDown = 0x00A1;
    private const uint WmNcRButtonDown = 0x00A4;
    private const uint WmNcMButtonDown = 0x00A7;
    private const uint WmNcXButtonDown = 0x00AB;
    private const uint WmNcPointerDown = 0x0242;
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
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const int SmCxSmallIcon = 49;
    private const int SmCySmallIcon = 50;
    private const uint ShowCommand = 1;
    private const uint ExitCommand = 2;
    private const int IdiApplication = 32512;

    private readonly IntPtr _windowHandle;
    private readonly Action _showWindow;
    private readonly Action _requestExit;
    private readonly int _minimumEffectiveClientWidth;
    private readonly int _minimumEffectiveClientHeight;
    private readonly WindowProcedure _windowProcedure;
    private IntPtr _previousWindowProcedure;
    private bool _subclassInstalled;
    private bool _trayIconAdded;
    private bool _ownsIconHandle;
    private NotifyIconData _iconData;
    private int _disposed;

    public ShellTrayIcon(
        IntPtr windowHandle,
        Action showWindow,
        Action requestExit,
        int minimumEffectiveClientWidth,
        int minimumEffectiveClientHeight)
    {
        if (minimumEffectiveClientWidth <= 0) throw new ArgumentOutOfRangeException(nameof(minimumEffectiveClientWidth));
        if (minimumEffectiveClientHeight <= 0) throw new ArgumentOutOfRangeException(nameof(minimumEffectiveClientHeight));

        _windowHandle = windowHandle;
        _showWindow = showWindow;
        _requestExit = requestExit;
        _minimumEffectiveClientWidth = minimumEffectiveClientWidth;
        _minimumEffectiveClientHeight = minimumEffectiveClientHeight;
        _windowProcedure = WindowProc;
        try
        {
            _previousWindowProcedure = SetWindowLongPtr(
                windowHandle,
                GwlWndProc,
                Marshal.GetFunctionPointerForDelegate(_windowProcedure));
            if (_previousWindowProcedure == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法安装 tray window procedure");
            }
            _subclassInstalled = true;

            var iconPath = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "Assets", "codex-usage-desktop.ico"));
            var iconHandle = LoadImage(
                IntPtr.Zero,
                iconPath,
                ImageIcon,
                GetSystemMetrics(SmCxSmallIcon),
                GetSystemMetrics(SmCySmallIcon),
                LrLoadFromFile);
            _ownsIconHandle = iconHandle != IntPtr.Zero;
            if (!_ownsIconHandle)
            {
                iconHandle = LoadIcon(IntPtr.Zero, new IntPtr(IdiApplication));
            }
            _iconData.IconHandle = iconHandle;

            _iconData = new NotifyIconData
            {
                Size = (uint)Marshal.SizeOf<NotifyIconData>(),
                WindowHandle = windowHandle,
                Id = 1,
                Flags = NifMessage | NifIcon | NifTip,
                CallbackMessage = TrayCallbackMessage,
                IconHandle = iconHandle,
                ToolTip = "Codex Usage Desktop",
                Info = string.Empty,
                InfoTitle = string.Empty,
            };

            if (!ShellNotifyIcon(NimAdd, ref _iconData))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建系统托盘图标");
            }
            _trayIconAdded = true;
        }
        catch
        {
            ReleaseNativeResources();
            throw;
        }
    }

    internal event Action? NonClientPointerPressed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ReleaseNativeResources();
    }

    private void ReleaseNativeResources()
    {
        try
        {
            foreach (var step in ShellResourceCleanupPlan.OrderedSteps(
                         _trayIconAdded,
                         _subclassInstalled,
                         _ownsIconHandle))
            {
                try
                {
                    ReleaseNativeResource(step);
                }
                catch (Exception error)
                {
                    Debug.WriteLine($"Shell resource cleanup step {step} failed: {error}");
                }
            }
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Shell resource cleanup planning failed: {error}");
        }
        finally
        {
            GC.KeepAlive(_windowProcedure);
        }
    }

    private void ReleaseNativeResource(ShellResourceCleanupStep step)
    {
        switch (step)
        {
            case ShellResourceCleanupStep.RemoveTrayIcon:
                _ = ShellNotifyIcon(NimDelete, ref _iconData);
                _trayIconAdded = false;
                break;
            case ShellResourceCleanupStep.RestoreWindowProcedure:
                var restoredProcedure = SetWindowLongPtr(_windowHandle, GwlWndProc, _previousWindowProcedure);
                if (restoredProcedure == IntPtr.Zero)
                {
                    Debug.WriteLine($"Window procedure restoration failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }
                else
                {
                    _subclassInstalled = false;
                }
                break;
            case ShellResourceCleanupStep.DestroyOwnedIcon:
                _ = DestroyIcon(_iconData.IconHandle);
                _iconData.IconHandle = IntPtr.Zero;
                _ownsIconHandle = false;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(step), step, null);
        }
    }

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmGetMinMaxInfo && lParam != IntPtr.Zero)
        {
            var result = CallWindowProc(_previousWindowProcedure, window, message, wParam, lParam);
            ApplyMinimumTrackSize(window, lParam);
            return result;
        }

        if (message is WmNcLButtonDown
            or WmNcRButtonDown
            or WmNcMButtonDown
            or WmNcXButtonDown
            or WmNcPointerDown)
        {
            NotifyNonClientPointerPressed();
        }

        if (message == TrayCallbackMessage)
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

    private void NotifyNonClientPointerPressed()
    {
        var subscribers = NonClientPointerPressed;
        if (subscribers is null) return;

        foreach (Action subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber();
            }
            catch (Exception error)
            {
                Debug.WriteLine($"Non-client pointer subscriber failed: {error}");
            }
        }
    }

    private void ApplyMinimumTrackSize(IntPtr window, IntPtr minMaxInfoPointer)
    {
        var minimum = WindowPixelMetrics.MinimumTrackSize(
            window,
            _minimumEffectiveClientWidth,
            _minimumEffectiveClientHeight);
        var constraints = Marshal.PtrToStructure<MinMaxInfo>(minMaxInfoPointer);
        constraints.MinimumTrackSize.X = Math.Max(constraints.MinimumTrackSize.X, minimum.Width);
        constraints.MinimumTrackSize.Y = Math.Max(constraints.MinimumTrackSize.Y, minimum.Height);
        Marshal.StructureToPtr(constraints, minMaxInfoPointer, fDeleteOld: false);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaximumSize;
        public Point MaximumPosition;
        public Point MinimumTrackSize;
        public Point MaximumTrackSize;
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

    [DllImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(
        IntPtr instance,
        string name,
        uint type,
        int desiredWidth,
        int desiredHeight,
        uint loadFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

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
