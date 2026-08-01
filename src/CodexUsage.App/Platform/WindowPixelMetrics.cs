using CodexUsage.Application;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace CodexUsage.App.Platform;

internal static class WindowPixelMetrics
{
    private const int GwlStyle = -16;
    private const int GwlExtendedStyle = -20;

    public static SizeInt32 ToPhysicalSize(IntPtr windowHandle, int effectiveWidth, int effectiveHeight)
    {
        var dpi = GetDpiForWindow(windowHandle);
        var pixels = DashboardWindowSizing.MinimumTrackClientPixels(effectiveWidth, effectiveHeight, dpi);
        return new SizeInt32(pixels.Width, pixels.Height);
    }

    public static SizeInt32 MinimumTrackSize(
        IntPtr windowHandle,
        int effectiveClientWidth,
        int effectiveClientHeight)
    {
        var dpi = GetDpiForWindow(windowHandle);
        if (dpi == 0) dpi = 96;

        var client = DashboardWindowSizing.MinimumTrackClientPixels(
            effectiveClientWidth,
            effectiveClientHeight,
            dpi);
        var bounds = new NativeRect(0, 0, client.Width, client.Height);
        var style = unchecked((uint)GetWindowLong(windowHandle, GwlStyle));
        var extendedStyle = unchecked((uint)GetWindowLong(windowHandle, GwlExtendedStyle));
        if (!AdjustWindowRectExForDpi(ref bounds, style, false, extendedStyle, dpi))
        {
            return new SizeInt32(client.Width, client.Height);
        }

        return new SizeInt32(
            checked(bounds.Right - bounds.Left),
            checked(bounds.Bottom - bounds.Top));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr windowHandle, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustWindowRectExForDpi(
        ref NativeRect bounds,
        uint style,
        [MarshalAs(UnmanagedType.Bool)] bool hasMenu,
        uint extendedStyle,
        uint dpi);
}
