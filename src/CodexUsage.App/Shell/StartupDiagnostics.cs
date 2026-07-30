using System.Text;

namespace CodexUsage.App.Shell;

internal static class StartupDiagnostics
{
    private const long MaximumLogBytes = 512 * 1024;
    private static readonly object Sync = new();
    private static string _logPath = Path.Combine(
        Path.GetTempPath(),
        "Codex Usage Desktop",
        "startup.log");

    public static string LogPath
    {
        get
        {
            lock (Sync)
            {
                return _logPath;
            }
        }
    }

    public static void Configure(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        lock (Sync)
        {
            _logPath = Path.Combine(dataDirectory, "logs", "startup.log");
        }
    }

    public static void InstallProcessHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteException("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            WriteException("TaskScheduler.UnobservedTaskException", args.Exception);
    }

    public static void Write(string phase)
    {
        try
        {
            string path;
            lock (Sync)
            {
                path = _logPath;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} tid={Environment.CurrentManagedThreadId} {phase}{Environment.NewLine}";
            lock (Sync)
            {
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumLogBytes)
                {
                    var previousPath = Path.Combine(
                        Path.GetDirectoryName(path)!,
                        $"{Path.GetFileNameWithoutExtension(path)}.previous{Path.GetExtension(path)}");
                    File.Move(path, previousPath, true);
                }

                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Startup diagnostics must never prevent the application from launching.
        }
    }

    public static void WriteException(string phase, Exception? exception)
    {
        Write(exception is null ? phase : $"{phase}{Environment.NewLine}{exception}");
    }
}
