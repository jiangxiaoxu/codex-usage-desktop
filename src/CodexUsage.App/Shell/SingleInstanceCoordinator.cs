using System.Collections.Concurrent;
using Microsoft.Windows.AppLifecycle;

namespace CodexUsage.App.Shell;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string InstanceKey = "CodexUsageDesktop.Native.Primary";
    private readonly AppInstance _instance;
    private readonly AppActivationArguments _initialActivation;
    private readonly ConcurrentQueue<AppActivationArguments> _pendingActivations = new();
    private Action<AppActivationArguments>? _activationHandler;
    private int _disposed;

    private SingleInstanceCoordinator(
        AppInstance instance,
        AppActivationArguments initialActivation,
        bool isCurrent)
    {
        _instance = instance;
        _initialActivation = initialActivation;
        IsCurrent = isCurrent;

        if (isCurrent)
        {
            _instance.Activated += OnActivated;
        }
    }

    public bool IsCurrent { get; }

    public static SingleInstanceCoordinator Acquire(string instanceKey = InstanceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceKey);
        var current = AppInstance.GetCurrent();
        var activation = current.GetActivatedEventArgs();
        var registered = AppInstance.FindOrRegisterForKey(instanceKey);
        return new SingleInstanceCoordinator(registered, activation, registered.IsCurrent);
    }

    public Task RedirectActivationAsync()
    {
        if (IsCurrent)
        {
            throw new InvalidOperationException("The current instance cannot redirect to itself.");
        }

        return _instance.RedirectActivationToAsync(_initialActivation).AsTask();
    }

    public void AttachActivationHandler(Action<AppActivationArguments> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(ref _activationHandler, handler);

        while (_pendingActivations.TryDequeue(out var activation))
        {
            handler(activation);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (IsCurrent)
        {
            _instance.Activated -= OnActivated;
        }
    }

    private void OnActivated(object? sender, AppActivationArguments activation)
    {
        var handler = Volatile.Read(ref _activationHandler);
        if (handler is null)
        {
            _pendingActivations.Enqueue(activation);
            return;
        }

        handler(activation);
    }
}
