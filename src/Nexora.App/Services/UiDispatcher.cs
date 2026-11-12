using Microsoft.UI.Dispatching;

namespace Nexora.App.Services;

/// <summary>Marshals work from background monitoring threads onto the UI thread.</summary>
public sealed class UiDispatcher
{
    private DispatcherQueue? _queue;

    public void Initialize(DispatcherQueue queue) => _queue = queue;

    public bool HasThreadAccess => _queue?.HasThreadAccess ?? false;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_queue is null)
        {
            return;
        }

        if (_queue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _queue.TryEnqueue(DispatcherQueuePriority.Normal, () => action());
        }
    }

    public DispatcherQueueTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var timer = (_queue ?? DispatcherQueue.GetForCurrentThread()).CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = true;
        timer.Tick += (_, _) => tick();
        return timer;
    }
}
