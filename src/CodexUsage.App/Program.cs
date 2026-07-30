using CodexUsage.App.Platform;
using CodexUsage.App.Shell;
using CodexUsage.Application;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace CodexUsage.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        StartupDiagnostics.InstallProcessHandlers();
        StartupDiagnostics.Write("Program.Main entered");
        ComWrappersSupport.InitializeComWrappers();
        StartupDiagnostics.Write("ComWrappers initialized");
        var isSmokeTest = args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        var isStartupLaunch = StartupLaunchContract.IsStartupLaunch(args);

        var instance = SingleInstanceCoordinator.Acquire(isSmokeTest
            ? $"CodexUsageDesktop.Native.Smoke.{Environment.ProcessId}"
            : "CodexUsageDesktop.Native.Primary");
        StartupDiagnostics.Write($"Single instance acquired; current={instance.IsCurrent}");
        if (!instance.IsCurrent)
        {
            try
            {
                Task.Run(instance.RedirectActivationAsync).GetAwaiter().GetResult();
                return 0;
            }
            finally
            {
                instance.Dispose();
            }
        }

        var paths = isSmokeTest ? ApplicationPaths.CreateSmokeTest() : ApplicationPaths.Resolve();
        StartupDiagnostics.Configure(paths.DataDirectory);
        StartupDiagnostics.Write($"Application paths resolved; data={paths.DataDirectory}");
        if (!LedgerOwnershipGuard.TryAcquire(paths.DataDirectory, out var ledgerGuard, out var ownershipError))
        {
            NativeDialog.ShowError("Codex Usage Desktop", ownershipError);
            instance.Dispose();
            return 2;
        }

        App? app = null;
        var exitCode = isSmokeTest ? 4 : 0;
        try
        {
            StartupDiagnostics.Write("Entering Application.Start");
            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                StartupDiagnostics.Write("Application.Start callback entered");
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
                StartupDiagnostics.Write("Dispatcher synchronization context installed");
                app = new App(instance, ledgerGuard, paths, isStartupLaunch, isSmokeTest);
                StartupDiagnostics.Write("App constructed");
            });

            StartupDiagnostics.Write("Application.Start returned");
            app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            exitCode = app?.ExitCode ?? exitCode;
        }
        finally
        {
            StartupDiagnostics.Write("Program cleanup");
            ledgerGuard.Dispose();
            instance.Dispose();
        }

        if (isSmokeTest && exitCode == 0)
        {
            paths.DeleteSmokeTestData();
        }

        return exitCode;
    }
}
