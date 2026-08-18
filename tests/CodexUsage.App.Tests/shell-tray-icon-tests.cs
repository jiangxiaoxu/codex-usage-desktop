using CodexUsage.App.Platform;
using Xunit;

namespace CodexUsage.App.Tests;

public sealed class ShellTrayIconTests
{
    [Theory]
    [InlineData(0xABCD0400L)]
    [InlineData(0x00010401L)]
    public void ClassifyNotificationRecognizesVersion4SelectionWithIconIdInHighWord(long callbackData)
    {
        Assert.Equal(TrayNotificationKind.ShowWindow, ShellTrayIcon.ClassifyNotification(new IntPtr(callbackData)));
    }

    [Theory]
    [InlineData(0x0202L)]
    [InlineData(0x0203L)]
    public void ClassifyNotificationRecognizesLegacyLeftButtonEvents(long callbackData)
    {
        Assert.Equal(TrayNotificationKind.ShowWindow, ShellTrayIcon.ClassifyNotification(new IntPtr(callbackData)));
    }

    [Theory]
    [InlineData(0x0205L)]
    [InlineData(0x007BL)]
    public void ClassifyNotificationRecognizesContextMenuEvents(long callbackData)
    {
        Assert.Equal(TrayNotificationKind.ShowContextMenu, ShellTrayIcon.ClassifyNotification(new IntPtr(callbackData)));
    }

    [Theory]
    [InlineData(0x0000L)]
    [InlineData(0x0402L)]
    [InlineData(0x0206L)]
    public void ClassifyNotificationIgnoresUnrecognizedEvents(long callbackData)
    {
        Assert.Equal(TrayNotificationKind.Ignore, ShellTrayIcon.ClassifyNotification(new IntPtr(callbackData)));
    }
}
