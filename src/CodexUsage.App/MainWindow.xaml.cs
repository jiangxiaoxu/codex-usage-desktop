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
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace CodexUsage.App;

public sealed partial class MainWindow : Window
{
    private const int MinimumEffectiveWidth = 900;
    private const int MinimumEffectiveHeight = 720;
    private const int InitialEffectiveWidth = 1440;
    private const int InitialEffectiveHeight = 1024;
    private readonly UISettings _uiSettings = new();
    private readonly HashSet<FrameworkElement> _scaledTableElements = [];
    private readonly DashboardViewportRefreshLifecycle _viewportRefreshLifecycle = new();
    private readonly AppWindow _appWindow;
    private readonly IntPtr _windowHandle;
    private readonly IUsageDashboardService _dashboardService;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ShellTrayIcon _trayIcon;
    private readonly Action _requestApplicationExit;
    private readonly HttpClient _updateHttpClient;
    private readonly IReleaseUpdateService _releaseUpdateService;
    private readonly bool _enableAutomaticUpdateChecks;
    private Task? _initializationTask;
    private Exception? _initializationFailure;
    private Task? _resourceDisposalTask;
    private bool _allowClose;
    private int _closed;
    private int _shuttingDown;
    private int _textScaleMonitoringActive;

    internal MainWindow(
        ApplicationPaths paths,
        Action requestApplicationExit,
        bool enableAutomaticUpdateChecks)
    {
        StartupDiagnostics.Write("MainWindow.InitializeComponent starting");
        InitializeComponent();
        StartupDiagnostics.Write("MainWindow.InitializeComponent completed");
        _requestApplicationExit = requestApplicationExit;
        _enableAutomaticUpdateChecks = enableAutomaticUpdateChecks;

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
            new DeferredProcessEfficiencyMode(),
            protectedPathPolicy);
        StartupDiagnostics.Write("Dashboard service constructed");
        IStartupRegistrationService startupTask = string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? new UnavailableStartupRegistrationService("无法确定当前可执行文件路径; 开机自启动不可用")
            : new WindowsRunStartupRegistrationService(Environment.ProcessPath);
        _updateHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        IReleaseUpdateService packageUpdate = new GitHubReleaseUpdateService(
            _updateHttpClient,
            paths.DataDirectory,
            CurrentReleaseVersion,
            protectedPathPolicy);
        _releaseUpdateService = packageUpdate;
        ViewModel = new DashboardViewModel(
            _dashboardService,
            new UiDispatcher(DispatcherQueue),
            startupTask,
            packageUpdate,
            new WinUiExportDestinationPicker(_windowHandle));
        ViewModel.SnapshotApplying += OnSnapshotApplying;
        ViewModel.SnapshotApplied += OnSnapshotApplied;
        StartupDiagnostics.Write("Dashboard view model constructed");
        WindowRoot.DataContext = ViewModel;
        Closed += OnWindowClosed;
        BodyRoot.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnBodyRootPointerPressed),
            handledEventsToo: true);
        BodyRoot.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnBodyRootPointerWheelChanged),
            handledEventsToo: true);
        BodyRoot.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(OnBodyRootKeyDown),
            handledEventsToo: true);

        ExtendsContentIntoTitleBar = true;
        StartupDiagnostics.Write("Setting custom title bar");
        SetTitleBar(AppTitleBar);
        StartupDiagnostics.Write("Custom title bar set");

        var windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        StartupDiagnostics.Write("AppWindow acquired");
        _appWindow.Title = "Codex Usage Desktop";
        var appIconPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "codex-usage-desktop.ico");
        try
        {
            if (File.Exists(appIconPath))
            {
                _appWindow.SetIcon(appIconPath);
            }
            else
            {
                StartupDiagnostics.Write($"AppWindow icon not found: {appIconPath}");
            }
        }
        catch (Exception error)
        {
            StartupDiagnostics.Write($"AppWindow icon unavailable: {error.Message}");
        }
        _appWindow.Resize(WindowPixelMetrics.ToPhysicalSize(
            _windowHandle,
            InitialEffectiveWidth,
            InitialEffectiveHeight));
        StartupDiagnostics.Write("Creating tray icon");
        _trayIcon = new ShellTrayIcon(
            _windowHandle,
            ShowFromTray,
            _requestApplicationExit,
            MinimumEffectiveWidth,
            MinimumEffectiveHeight);
        _uiSettings.TextScaleFactorChanged += OnTextScaleFactorChanged;
        Volatile.Write(ref _textScaleMonitoringActive, 1);
        StartupDiagnostics.Write("MainWindow constructor completed");
    }

    public DashboardViewModel ViewModel { get; }

    private static string CurrentReleaseVersion
    {
        get
        {
            var version = typeof(MainWindow).Assembly.GetName().Version;
            return version is { Major: >= 0, Minor: >= 0, Build: >= 0 }
                ? $"{version.Major}.{version.Minor}.{version.Build}"
                : "0.3.1";
        }
    }

    internal void BeginInitialize()
    {
        if (_enableAutomaticUpdateChecks)
        {
            ViewModel.StartAutomaticUpdateChecks(_lifetime.Token);
        }

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
        if (Volatile.Read(ref _closed) != 0 || Volatile.Read(ref _shuttingDown) != 0)
        {
            return;
        }

        if (_appWindow.Presenter is OverlappedPresenter
            {
                State: OverlappedPresenterState.Minimized,
            } presenter)
        {
            presenter.Restore();
        }

        _appWindow.Show();
        Activate();
    }

    internal async Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        Interlocked.Exchange(ref _shuttingDown, 1);
        ApplyViewportRefreshTransition(_viewportRefreshLifecycle.Cancel());
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
        ApplyViewportRefreshTransition(_viewportRefreshLifecycle.Cancel());
        StopTextScaleMonitoring();
        _trayIcon.Dispose();
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

    private void OnModelValuesViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
    {
        if (sender is ScrollViewer values)
        {
            ModelHeaderScroll.ChangeView(values.HorizontalOffset, null, null, disableAnimation: true);
        }
    }

    private void OnRoleValuesViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
    {
        if (sender is ScrollViewer values)
        {
            RoleHeaderScroll.ChangeView(values.HorizontalOffset, null, null, disableAnimation: true);
        }
    }

    private void OnBodyRootViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
    {
        UpdateStickyHeader(ModelTable, ModelTableHeader, ModelTableHeaderTransform);
        UpdateStickyHeader(RoleTable, RoleTableHeader, RoleTableHeaderTransform);
    }

    private void OnSnapshotApplying(object? sender, DashboardSnapshotApplicationEventArgs args)
    {
        ApplyViewportRefreshTransition(_viewportRefreshLifecycle.BeginSnapshotApplication(
            args,
            () => BodyRoot.VerticalOffset));
    }

    private void OnSnapshotApplied(object? sender, DashboardSnapshotApplicationEventArgs args)
    {
        ApplyViewportRefreshTransition(_viewportRefreshLifecycle.CompleteSnapshotApplication(args));
    }

    private void OnBodyRootLayoutUpdated(object? sender, object args) =>
        ApplyViewportRefreshTransition(_viewportRefreshLifecycle.CompleteLayout(BodyRoot.ScrollableHeight));

    private void ApplyViewportRefreshTransition(DashboardViewportRefreshTransition transition)
    {
        if (transition.UnsubscribeLayoutUpdated)
            BodyRoot.LayoutUpdated -= OnBodyRootLayoutUpdated;
        if (transition.SubscribeLayoutUpdated)
            BodyRoot.LayoutUpdated += OnBodyRootLayoutUpdated;

        if (transition.VerticalOffsetToRestore is not { } targetOffset) return;

        BodyRoot.ChangeView(null, targetOffset, null, disableAnimation: true);
        UpdateStickyHeader(ModelTable, ModelTableHeader, ModelTableHeaderTransform);
        UpdateStickyHeader(RoleTable, RoleTableHeader, RoleTableHeaderTransform);
    }

    private void OnBodyRootPointerPressed(object sender, PointerRoutedEventArgs args) =>
        ApplyViewportRefreshTransition(_viewportRefreshLifecycle.RecordUserInteraction());

    private void OnBodyRootPointerWheelChanged(object sender, PointerRoutedEventArgs args) =>
        ApplyViewportRefreshTransition(_viewportRefreshLifecycle.RecordUserInteraction());

    private void OnBodyRootKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Up
            or VirtualKey.Down
            or VirtualKey.PageUp
            or VirtualKey.PageDown
            or VirtualKey.Home
            or VirtualKey.End
            or VirtualKey.Space)
        {
            ApplyViewportRefreshTransition(_viewportRefreshLifecycle.RecordUserInteraction());
        }
    }

    private void OnTableCellLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement element)
        {
            if (_scaledTableElements.Add(element))
            {
                element.Unloaded += OnTableCellUnloaded;
            }

            ApplyCurrentTableRowHeight(element);
        }
    }

    private void OnTableCellUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement element && _scaledTableElements.Remove(element))
        {
            element.Unloaded -= OnTableCellUnloaded;
        }
    }

    private void OnTextScaleFactorChanged(UISettings sender, object args)
    {
        if (Volatile.Read(ref _textScaleMonitoringActive) == 0) return;

        _ = DispatcherQueue.TryEnqueue(RefreshTableLayoutForTextScale);
    }

    private void RefreshTableLayoutForTextScale()
    {
        if (Volatile.Read(ref _textScaleMonitoringActive) == 0) return;

        foreach (var element in _scaledTableElements)
        {
            ApplyCurrentTableRowHeight(element);
        }

        UpdateStickyHeader(ModelTable, ModelTableHeader, ModelTableHeaderTransform);
        UpdateStickyHeader(RoleTable, RoleTableHeader, RoleTableHeaderTransform);
    }

    private void ApplyCurrentTableRowHeight(FrameworkElement element)
    {
        element.Height = DashboardAccessibilityLayout.TableRowHeight(_uiSettings.TextScaleFactor);
    }

    private void StopTextScaleMonitoring()
    {
        if (Interlocked.Exchange(ref _textScaleMonitoringActive, 0) == 0) return;

        _uiSettings.TextScaleFactorChanged -= OnTextScaleFactorChanged;
        foreach (var element in _scaledTableElements)
        {
            element.Unloaded -= OnTableCellUnloaded;
        }
        _scaledTableElements.Clear();
    }

    private void OnStickyTableSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (ReferenceEquals(sender, ModelTable))
        {
            UpdateStickyHeader(ModelTable, ModelTableHeader, ModelTableHeaderTransform);
        }
        else if (ReferenceEquals(sender, RoleTable))
        {
            UpdateStickyHeader(RoleTable, RoleTableHeader, RoleTableHeaderTransform);
        }
    }

    private void UpdateStickyHeader(
        FrameworkElement table,
        FrameworkElement header,
        TranslateTransform transform)
    {
        if (table.ActualHeight <= 0 || header.ActualHeight <= 0) return;

        var tableTop = table.TransformToVisual(BodyRoot).TransformPoint(new Windows.Foundation.Point()).Y;
        var renderedHeaderTop = header.TransformToVisual(BodyRoot).TransformPoint(new Windows.Foundation.Point()).Y;
        var naturalHeaderTop = renderedHeaderTop - transform.Y;
        var headerOffsetWithinTable = naturalHeaderTop - tableTop;
        var maximumTranslation = Math.Max(
            0,
            table.ActualHeight - headerOffsetWithinTable - header.ActualHeight);
        transform.Y = Math.Clamp(-naturalHeaderTop, 0, maximumTranslation);
    }

    private void OnCheckUpdateRequested(object sender, RoutedEventArgs args)
    {
        _ = RunUiOperationAsync(
            () => ViewModel.CheckForUpdatesAsync(_lifetime.Token),
            "Update check");
    }

    private void OnDownloadUpdateRequested(object sender, RoutedEventArgs args)
    {
        _ = RunUiOperationAsync(
            () => ViewModel.DownloadUpdateAsync(_lifetime.Token),
            "Update download");
    }

    private void OnRunUpdateRequested(object sender, RoutedEventArgs args)
    {
        _ = RunDownloadedInstallerAsync();
    }

    private async Task RunDownloadedInstallerAsync()
    {
        if (!ViewModel.TryBeginInstallerLaunch(
                out var installerPath,
                out var package,
                out var updateStateGeneration))
        {
            return;
        }
        try
        {
            var confirmation = new ContentDialog
            {
                XamlRoot = WindowRoot.XamlRoot,
                Title = "运行未签名安装器",
                Content = "安装器未进行 Authenticode 签名,Windows 可能显示 Unknown Publisher 或 SmartScreen。继续后,NSIS 安装器会结束当前 Codex Usage Desktop process 和 collector 以完成升级。",
                PrimaryButtonText = "运行安装器",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

            if (!ViewModel.IsDownloadedUpdateCurrent(installerPath, package, updateStateGeneration))
            {
                ViewModel.ReportUpdateInstallerLaunchBlocked("更新状态已变化; 未启动安装器");
                return;
            }

            var verification = await _releaseUpdateService.VerifyDownloadedInstallerAsync(
                package,
                installerPath,
                _lifetime.Token);
            if (!verification.IsValid)
            {
                ViewModel.ReportUpdateInstallerLaunchBlocked(verification.Message);
                return;
            }

            if (!ViewModel.IsDownloadedUpdateCurrent(installerPath, package, updateStateGeneration))
            {
                ViewModel.ReportUpdateInstallerLaunchBlocked("更新状态已变化; 未启动安装器");
                return;
            }

            Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            ViewModel.ReportUpdateInstallerLaunchFailure(error);
        }
        finally
        {
            ViewModel.CompleteInstallerLaunch();
        }
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
            StopTextScaleMonitoring();
            return;
        }

        args.Handled = true;
        _appWindow.Hide();
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
            ViewModel.SnapshotApplying -= OnSnapshotApplying;
            ViewModel.SnapshotApplied -= OnSnapshotApplied;
            await ViewModel.StopAutomaticUpdateChecksAsync();
            ViewModel.Dispose();
        }

        await _dashboardService.DisposeAsync();
        _updateHttpClient.Dispose();
        _lifetime.Dispose();
    }

    private sealed class DeferredProcessEfficiencyMode : IProcessEfficiencyMode
    {
        public ProcessEfficiencyModeResult TryApply(ProcessExecutionMode mode) => new(
            mode,
            false,
            false,
            "Process scheduling changes are deferred");
    }

}
