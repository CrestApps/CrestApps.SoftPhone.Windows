using System.Threading;

namespace SoftPhone.App;

/// <summary>
/// Single-instance guard. The first instance owns a named mutex and listens on a named
/// event; a second launch signals that event (so the running tray app opens/focuses the
/// phone) and then exits. Contract §8 "Single instance".
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\CrestApps.SoftPhone.Instance";
    private const string EventName = @"Local\CrestApps.SoftPhone.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private RegisteredWaitHandle? _registration;

    public bool IsPrimary { get; }

    private SingleInstance(Mutex mutex, bool isPrimary, EventWaitHandle activateEvent)
    {
        _mutex = mutex;
        IsPrimary = isPrimary;
        _activateEvent = activateEvent;
    }

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        var evt = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        return new SingleInstance(mutex, createdNew, evt);
    }

    /// <summary>Primary: invoke <paramref name="onActivate"/> when another instance launches.</summary>
    public void ListenForActivation(Action onActivate)
    {
        if (!IsPrimary) return;
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => onActivate(),
            state: null,
            timeout: Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);
    }

    /// <summary>Secondary: wake the primary instance, then this process should exit.</summary>
    public void SignalPrimary() => _activateEvent.Set();

    public void Dispose()
    {
        _registration?.Unregister(null);
        try { if (IsPrimary) _mutex.ReleaseMutex(); } catch { /* ignore */ }
        _mutex.Dispose();
        _activateEvent.Dispose();
    }
}
