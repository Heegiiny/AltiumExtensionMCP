using System;
using System.Threading;
using System.Windows.Forms;
using AltiumMcp.Contracts.Bridge;

namespace AltiumMcp.Extension.Bridge;

/// <summary>
/// Marshals work onto Altium's main (UI) thread. All Altium COM interfaces (IClient, IWorkspace, ISch_*, IPCB_*)
/// are single-threaded apartment objects and must only be touched from the thread that created the server module.
/// Implementation: a hidden WinForms control whose handle lives on the UI thread; Control.BeginInvoke posts a
/// window message which the host's (VCL) message loop dispatches.
/// </summary>
public sealed class UiThreadDispatcher : IDisposable
{
    private readonly Control _control;
    private readonly int _uiThreadId;

    /// <summary>Must be constructed on the UI thread (e.g. inside InvokePluginFactory / ServerModule ctor).</summary>
    public UiThreadDispatcher()
    {
        _uiThreadId = Thread.CurrentThread.ManagedThreadId;
        _control = new Control();
        // Force handle creation now, on the UI thread.
        _ = _control.Handle;
    }

    public int UiThreadId => _uiThreadId;

    public bool IsOnUiThread => Thread.CurrentThread.ManagedThreadId == _uiThreadId;

    /// <summary>
    /// Runs <paramref name="func"/> on the UI thread and waits up to <paramref name="timeout"/>.
    /// Throws <see cref="BridgeException"/> with code ALTIUM_BUSY on timeout (Altium is blocked in a modal
    /// operation, long compile, etc.). Exceptions thrown by <paramref name="func"/> are propagated.
    /// </summary>
    public T Invoke<T>(Func<T> func, TimeSpan timeout)
    {
        if (IsOnUiThread)
        {
            return func();
        }

        if (_control.IsDisposed)
        {
            throw new BridgeException(BridgeErrorCodes.Internal, "UI dispatcher is disposed (extension unloading).");
        }

        IAsyncResult ar = _control.BeginInvoke(new Func<object?>(() => func()));
        if (!ar.AsyncWaitHandle.WaitOne(timeout))
        {
            throw new BridgeException(
                BridgeErrorCodes.AltiumBusy,
                $"Altium UI thread did not respond within {timeout.TotalSeconds:0}s. It may be busy (compiling, modal dialog, long operation). Retry later.");
        }

        object? result = _control.EndInvoke(ar);
        return (T)result!;
    }

    public void Invoke(Action action, TimeSpan timeout)
    {
        Invoke<object?>(() =>
        {
            action();
            return null;
        }, timeout);
    }

    /// <summary>Fire-and-forget on the UI thread (used for UI updates from background threads).</summary>
    public void Post(Action action)
    {
        if (_control.IsDisposed)
        {
            return;
        }

        if (IsOnUiThread)
        {
            action();
            return;
        }

        try
        {
            _control.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            BridgeLog.Warn($"Post to UI thread failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            if (IsOnUiThread)
            {
                _control.Dispose();
            }
        }
        catch
        {
            // ignore
        }
    }
}
