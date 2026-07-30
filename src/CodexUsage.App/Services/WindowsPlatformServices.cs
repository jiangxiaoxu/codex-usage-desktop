using System.Security;
using CodexUsage.Application;
using Microsoft.Win32;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace CodexUsage.App.Services;

public sealed class WindowsRunStartupRegistrationService : IStartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Codex Usage Desktop";
    private readonly string _executablePath;
    private readonly string _runCommand;

    public WindowsRunStartupRegistrationService(string executablePath)
    {
        _executablePath = Path.GetFullPath(executablePath);
        _runCommand = StartupLaunchContract.CreateRunCommand(_executablePath);
    }

    public Task<PlatformFeatureResult> GetStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var command = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            var enabled = StartupLaunchContract.IsOwnedRunCommand(command, _executablePath);
            var message = enabled
                ? "开机自启动已开启; 登录后将在后台启动"
                : command is null
                    ? "开机自启动已关闭"
                    : "检测到旧的自启动命令; 重新开启将迁移到当前 WinUI 版本";
            return Task.FromResult(new PlatformFeatureResult(true, enabled, message));
        }
        catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
        {
            return Task.FromResult(Unavailable($"无法读取开机自启动设置: {error.Message}"));
        }
    }

    public Task<PlatformFeatureResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, _runCommand, RegistryValueKind.String);
                return Task.FromResult(new PlatformFeatureResult(
                    true,
                    true,
                    "开机自启动已开启; 登录后将在后台启动"));
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return Task.FromResult(new PlatformFeatureResult(true, false, "开机自启动已关闭"));
        }
        catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
        {
            return Task.FromResult(Unavailable($"无法修改开机自启动设置: {error.Message}"));
        }
    }

    private static PlatformFeatureResult Unavailable(string message) => new(false, false, message);
}

public sealed class WinUiExportDestinationPicker(IntPtr windowHandle) : IExportDestinationPicker
{
    public async Task<string?> PickCsvPathAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new FileSavePicker
        {
            SuggestedFileName = $"codex-usage-{DateTime.Now:yyyyMMdd-HHmmss}",
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add("CSV", [".csv"]);
        InitializeWithWindow.Initialize(picker, windowHandle);
        var file = await picker.PickSaveFileAsync().AsTask(cancellationToken);
        return file?.Path;
    }
}
