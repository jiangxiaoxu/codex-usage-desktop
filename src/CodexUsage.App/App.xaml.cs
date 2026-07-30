using System.Diagnostics;
using CodexUsage.App.Platform;
using CodexUsage.App.Shell;
using CodexUsage.Application;
using Microsoft.Windows.AppLifecycle;
using Microsoft.UI.Xaml;

namespace CodexUsage.App;

public partial class App : Microsoft.UI.Xaml.Application, IAsyncDisposable
{
    private readonly SingleInstanceCoordinator _singleInstance;
    private readonly LedgerOwnershipGuard _ledgerGuard;
    private readonly ApplicationPaths _paths;
    private readonly bool _isStartupLaunch;
    private readonly bool _isSmokeTest;
    private readonly object _shutdownLock = new();
    private MainWindow? _window;
    private Task? _shutdownTask;

    internal App(
        SingleInstanceCoordinator singleInstance,
        LedgerOwnershipGuard ledgerGuard,
        ApplicationPaths paths,
        bool isStartupLaunch,
        bool isSmokeTest)
    {
        _singleInstance = singleInstance;
        _ledgerGuard = ledgerGuard;
        _paths = paths;
        _isStartupLaunch = isStartupLaunch;
        _isSmokeTest = isSmokeTest;
        UnhandledException += OnUnhandledException;
        StartupDiagnostics.Write("App.InitializeComponent starting");
        InitializeComponent();
        StartupDiagnostics.Write("App.InitializeComponent completed");
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        StartupDiagnostics.Write("App.OnLaunched entered");
        _window = new MainWindow(_paths, BeginExit);
        StartupDiagnostics.Write("MainWindow constructed");
        _window.BeginInitialize();
        StartupDiagnostics.Write("MainWindow initialization started");
        _singleInstance.AttachActivationHandler(OnRedirectedActivation);

        if (!_isStartupLaunch || _isSmokeTest)
        {
            StartupDiagnostics.Write("Showing MainWindow");
            _window.ShowFromTray();
            StartupDiagnostics.Write("MainWindow shown");
        }

        if (_isSmokeTest)
        {
            _ = CompleteSmokeTestAsync();
        }
    }

    internal int ExitCode { get; private set; }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        StartupDiagnostics.WriteException("Xaml.UnhandledException", args.Exception);
    }

    public ValueTask DisposeAsync() => new(EnsureShutdownStarted());

    private void OnRedirectedActivation(AppActivationArguments activation)
    {
        var window = _window;
        if (window is null || IsStartupActivation(activation))
        {
            return;
        }

        _ = window.DispatcherQueue.TryEnqueue(window.ShowFromTray);
    }

    private static bool IsStartupActivation(AppActivationArguments activation) =>
        activation.Kind == ExtendedActivationKind.Launch
        && activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
        && StartupLaunchContract.IsStartupLaunch(launch.Arguments);

    private void BeginExit()
    {
        _ = EnsureShutdownStarted();
    }

    private async Task CompleteSmokeTestAsync()
    {
        try
        {
            await (_window ?? throw new InvalidOperationException("The smoke-test window was not created."))
                .WaitUntilInitializedAsync(TimeSpan.FromSeconds(20));
            StartupDiagnostics.Write("Smoke test completed successfully");
            ExitCode = 0;
        }
        catch (Exception error)
        {
            ExitCode = 4;
            StartupDiagnostics.WriteException("Smoke test failed", error);
        }
        finally
        {
            BeginExit();
        }
    }

    private Task EnsureShutdownStarted()
    {
        lock (_shutdownLock)
        {
            _shutdownTask ??= ShutdownAsync();
            return _shutdownTask;
        }
    }

    private async Task ShutdownAsync()
    {
        try
        {
            if (_window is not null && !await _window.ShutdownAsync(TimeSpan.FromSeconds(6)))
            {
                Debug.WriteLine("Application cleanup did not complete within the shutdown budget; terminating while the ledger mutex is still owned.");
                _singleInstance.Dispose();
                Environment.Exit(3);
                return;
            }

            _singleInstance.Dispose();
            _ledgerGuard.Dispose();
            _window?.CloseForExit();
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Application shutdown failed: {error}");
            _singleInstance.Dispose();
            Environment.Exit(3);
        }
    }
}
