using Microsoft.UI.Dispatching;

namespace CodexUsage.App.Services;

public interface IUiDispatcher
{
    bool TryEnqueue(Action action);
}

public sealed class UiDispatcher(DispatcherQueue dispatcherQueue) : IUiDispatcher
{
    private readonly DispatcherQueue _dispatcherQueue = dispatcherQueue
        ?? throw new ArgumentNullException(nameof(dispatcherQueue));

    public bool TryEnqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _dispatcherQueue.TryEnqueue(() => action());
    }
}
