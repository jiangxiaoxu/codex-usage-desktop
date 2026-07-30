using System.Security.Cryptography;
using System.Text;

namespace CodexUsage.App.Platform;

internal sealed class LedgerOwnershipGuard : IDisposable
{
    private readonly Mutex _mutex;
    private int _disposed;

    private LedgerOwnershipGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static bool TryAcquire(
        string dataDirectory,
        out LedgerOwnershipGuard guard,
        out string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var normalizedPath = Path.GetFullPath(dataDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
        var mutex = new Mutex(false, $"Local\\CodexUsageDesktop.Ledger.{pathHash}");

        var acquired = false;
        try
        {
            acquired = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            guard = null!;
            errorMessage = $"账目目录正在被另一个 Codex Usage Desktop 实例使用。请先退出其他实例，再重试。\n\n账目目录: {normalizedPath}";
            return false;
        }

        guard = new LedgerOwnershipGuard(mutex);
        errorMessage = string.Empty;
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        finally
        {
            _mutex.Dispose();
        }
    }
}
