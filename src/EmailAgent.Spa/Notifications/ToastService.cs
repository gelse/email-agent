namespace EmailAgent.Spa.Notifications;

/// <summary>Severity of one toast — drives the CSS class and the live-region role.</summary>
public enum ToastKind
{
    Success,
    Info,
    Error,
}

/// <summary>One rendered toast: identity for dismissal, plus its severity and text.</summary>
public sealed record ToastItem(Guid Id, ToastKind Kind, string Message);

/// <summary>
/// M2T6: the singleton notification queue behind the sources screens (task
/// scope bullet 4). Thread-safe list of toasts plus a <see cref="Changed"/>
/// event the <c>ToastContainer</c> in <c>MainLayout</c> subscribes to;
/// success and info toasts auto-dismiss after
/// <see cref="AutoDismissMilliseconds"/>, errors stay until the user
/// dismisses them (task Risks "Toast accessibility").
/// </summary>
/// <remarks>
/// Auto-dismiss is a plain <see cref="Task.Delay"/> continuation — no JS
/// interop, so the same behaviour is observable in bUnit. Messages are
/// display text only: nothing here ever carries a passphrase or a DEK.
/// </remarks>
public sealed class ToastService
{
    /// <summary>Success/info lifetime (5 s per the task's Risks bullet).</summary>
    public const int AutoDismissMilliseconds = 5000;

    private readonly object _gate = new();
    private readonly List<ToastItem> _toasts = new();

    /// <summary>Raised after every add or dismissal (from an arbitrary thread).</summary>
    public event Action? Changed;

    /// <summary>A snapshot of the live toasts, oldest first.</summary>
    public IReadOnlyList<ToastItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _toasts.ToArray();
            }
        }
    }

    public void ShowSuccess(string message) => Show(ToastKind.Success, message);

    public void ShowInfo(string message) => Show(ToastKind.Info, message);

    public void ShowError(string message) => Show(ToastKind.Error, message);

    /// <summary>Removes one toast (manual dismiss — the only way an error goes away).</summary>
    public void Dismiss(Guid id)
    {
        lock (_gate)
        {
            _toasts.RemoveAll(toast => toast.Id == id);
        }

        Changed?.Invoke();
    }

    private void Show(ToastKind kind, string message)
    {
        var item = new ToastItem(Guid.NewGuid(), kind, message ?? string.Empty);
        lock (_gate)
        {
            _toasts.Add(item);
        }

        Changed?.Invoke();

        if (kind != ToastKind.Error)
        {
            _ = DismissAfterAsync(item.Id);
        }
    }

    private async Task DismissAfterAsync(Guid id)
    {
        await Task.Delay(AutoDismissMilliseconds).ConfigureAwait(false);
        Dismiss(id);
    }
}
