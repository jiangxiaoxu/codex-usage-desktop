using System.Diagnostics;
using CodexUsage.App.Platform;
using CodexUsage.App.Services;
using CodexUsage.App.Shell;
using CodexUsage.App.ViewModels;
using CodexUsage.Application;
using CodexUsage.Infrastructure;
using CodexUsage.Infrastructure.Collection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace CodexUsage.App;

public sealed partial class MainWindow : Window
{
    private const int MinimumEffectiveWidth = 720;
    private const int MinimumEffectiveHeight = 560;
    private const int InitialEffectiveWidth = 1440;
    private const int InitialEffectiveHeight = 1024;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _windowHandle;
    private readonly IUsageDashboardService _dashboardService;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ShellTrayIcon _trayIcon;
    private readonly Action _requestApplicationExit;
    private Task? _initializationTask;
    private Exception? _initializationFailure;
    private Task? _resourceDisposalTask;
    private bool _allowClose;
    private int _closed;

    internal MainWindow(ApplicationPaths paths, Action requestApplicationExit)
    {
        StartupDiagnostics.Write("MainWindow.InitializeComponent starting");
        InitializeComponent();
        StartupDiagnostics.Write("MainWindow.InitializeComponent completed");
        _requestApplicationExit = requestApplicationExit;

        var protectedPathPolicy = ProtectedPathPolicy.ForCodexHome(paths.CodexHome);
        StartupDiagnostics.Write("Protected path policy constructed");
        var collector = new UsageCollector(new CollectorOptions
        {
            CodexHome = paths.CodexHome,
            DatabasePath = Path.Combine(paths.DataDirectory, "usage.sqlite"),
            FullInventoryInterval = TimeSpan.FromMinutes(5),
        });
        StartupDiagnostics.Write("UsageCollector constructed");
        _windowHandle = WindowNative.GetWindowHandle(this);
        StartupDiagnostics.Write($"Window handle acquired; hwnd=0x{_windowHandle.ToInt64():X}");
        _dashboardService = new DashboardApplicationService(
            collector,
            new WindowsProcessEfficiencyMode(),
            protectedPathPolicy);
        StartupDiagnostics.Write("Dashboard service constructed");
        IStartupRegistrationService startupTask = string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? new UnavailableStartupRegistrationService("无法确定当前可执行文件路径; 开机自启动不可用")
            : new WindowsRunStartupRegistrationService(Environment.ProcessPath);
        IReleaseUpdateService packageUpdate = new UnconfiguredReleaseUpdateService();
        ViewModel = new DashboardViewModel(
            _dashboardService,
            new UiDispatcher(DispatcherQueue),
            startupTask,
            packageUpdate,
            new WinUiExportDestinationPicker(_windowHandle));
        StartupDiagnostics.Write("Dashboard view model constructed");
        WindowRoot.DataContext = ViewModel;
        Closed += OnWindowClosed;

        ExtendsContentIntoTitleBar = true;
        StartupDiagnostics.Write("Setting custom title bar");
        SetTitleBar(AppTitleBar);
        StartupDiagnostics.Write("Custom title bar set");

        var windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        StartupDiagnostics.Write("AppWindow acquired");
        _appWindow.Title = "Codex Usage Desktop";
        _appWindow.Resize(WindowPixelMetrics.ToPhysicalSize(
            _windowHandle,
            InitialEffectiveWidth,
            InitialEffectiveHeight));
        _appWindow.Changed += OnAppWindowChanged;
        StartupDiagnostics.Write("Creating tray icon");
        _trayIcon = new ShellTrayIcon(
            _windowHandle,
            ShowFromTray,
            _requestApplicationExit,
            OnNativeDpiChanged);
        StartupDiagnostics.Write("MainWindow constructor completed");
    }

    public DashboardViewModel ViewModel { get; }

    internal void BeginInitialize()
    {
        _initializationTask ??= InitializeSafelyAsync();
    }

    internal async Task WaitUntilInitializedAsync(TimeSpan timeout)
    {
        var initialization = _initializationTask
            ?? throw new InvalidOperationException("Window initialization has not started.");
        await initialization.WaitAsync(timeout);
        if (_initializationFailure is not null)
        {
            throw new InvalidOperationException("Dashboard initialization failed.", _initializationFailure);
        }

        await Task.Yield();
    }

    internal void ShowFromTray()
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return;
        }

        _appWindow.Show();
        Activate();
    }

    internal async Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        _lifetime.Cancel();
        _resourceDisposalTask ??= DisposeOwnedResourcesAsync();

        var timeoutTask = Task.Delay(timeout);
        var completed = await Task.WhenAny(_resourceDisposalTask, timeoutTask);
        if (completed == _resourceDisposalTask)
        {
            await _resourceDisposalTask;
            return true;
        }

        Debug.WriteLine($"Collector shutdown exceeded the {timeout.TotalSeconds:F0}s budget; process shutdown will continue.");
        return false;
    }

    internal void CloseForExit()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _allowClose = true;
        _trayIcon.Dispose();
        _appWindow.Changed -= OnAppWindowChanged;
        Close();
    }

    private async Task InitializeSafelyAsync()
    {
        try
        {
            await ViewModel.InitializeAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _initializationFailure = error;
            Debug.WriteLine($"Dashboard initialization failed: {error}");
            StartupDiagnostics.WriteException("Dashboard initialization failed", error);
        }
    }

    private void OnSyncRequested(object sender, RoutedEventArgs args)
    {
        _ = RunUiOperationAsync(
            () => ViewModel.SynchronizeAsync(_lifetime.Token),
            "Sync");
    }

    private void OnExportRequested(object sender, RoutedEventArgs args)
    {
        _ = ExportCsvSafelyAsync();
    }

    private void OnCheckUpdateRequested(object sender, RoutedEventArgs args)
    {
        _ = RunUiOperationAsync(
            () => ViewModel.CheckForUpdatesAsync(_lifetime.Token),
            "Update check");
    }

    private async Task ExportCsvSafelyAsync()
    {
        try
        {
            await ViewModel.ExportCsvAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Debug.WriteLine($"CSV export failed: {error}");
        }
    }

    private async Task RunUiOperationAsync(Func<Task> operation, string operationName)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Debug.WriteLine($"{operationName} failed: {error}");
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Handled = true;
        _appWindow.Hide();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange)
        {
            EnforceMinimumSize();
        }
    }

    private void OnNativeDpiChanged()
    {
        _ = DispatcherQueue.TryEnqueue(EnforceMinimumSize);
    }

    private void EnforceMinimumSize()
    {
        var minimum = WindowPixelMetrics.ToPhysicalSize(
            _windowHandle,
            MinimumEffectiveWidth,
            MinimumEffectiveHeight);
        var size = _appWindow.Size;
        var width = Math.Max(size.Width, minimum.Width);
        var height = Math.Max(size.Height, minimum.Height);

        if (width != size.Width || height != size.Height)
        {
            _appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        }
    }

    private async Task DisposeOwnedResourcesAsync()
    {
        try
        {
            if (_initializationTask is not null)
            {
                await _initializationTask;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            ViewModel.Dispose();
        }

        await _dashboardService.DisposeAsync();
        _lifetime.Dispose();
    }
}
