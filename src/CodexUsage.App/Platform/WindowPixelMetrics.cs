using System.Runtime.InteropServices;
using Windows.Graphics;

namespace CodexUsage.App.Platform;

internal static class WindowPixelMetrics
{
    private const uint DefaultDpi = 96;

    public static SizeInt32 ToPhysicalSize(IntPtr windowHandle, int effectiveWidth, int effectiveHeight)
    {
        var dpi = GetDpiForWindow(windowHandle);
        if (dpi == 0)
        {
            dpi = DefaultDpi;
        }

        return new SizeInt32(
            EffectiveToPhysical(effectiveWidth, dpi),
            EffectiveToPhysical(effectiveHeight, dpi));
    }

    internal static int EffectiveToPhysical(int effectivePixels, uint dpi) =>
        checked((int)Math.Ceiling(effectivePixels * dpi / (double)DefaultDpi));

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);
}
