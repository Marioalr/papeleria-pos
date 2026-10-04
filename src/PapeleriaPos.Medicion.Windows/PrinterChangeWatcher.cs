// Traído de Control Print v1.12 (ControlPrint.Core): se conservan sus nombres en inglés.
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static PapeleriaPos.Medicion.NativeMethods;

namespace PapeleriaPos.Medicion;

/// <summary>
/// Avisa cuando cambia algún trabajo de la cola, sin sondeo: espera sobre el handle de
/// FindFirstPrinterChangeNotification en el pool de hilos (0 % de CPU en reposo).
/// </summary>
public sealed class PrinterChangeWatcher : IDisposable
{
    readonly SafePrinterHandle printer;
    readonly IntPtr change;
    readonly EventWaitHandle waitHandle;
    readonly Action onChange;
    readonly Action<Exception> onError;
    RegisteredWaitHandle? registration;
    volatile bool disposed;

    public PrinterChangeWatcher(string queueName, Action onChange, Action<Exception> onError)
    {
        this.onChange = onChange;
        this.onError = onError;
        printer = PrintQueueClient.Open(queueName);
        change = FindFirstPrinterChangeNotification(printer, PRINTER_CHANGE_JOB, 0, IntPtr.Zero);
        if (change == IntPtr.Zero || change == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            printer.Dispose();
            throw new Win32Exception(err, "FindFirstPrinterChangeNotification");
        }
        waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset)
        {
            SafeWaitHandle = new SafeWaitHandle(change, ownsHandle: false)
        };
        Register();
    }

    void Register() =>
        registration = ThreadPool.RegisterWaitForSingleObject(waitHandle, Signaled, null, Timeout.Infinite, executeOnlyOnce: true);

    void Signaled(object? state, bool timedOut)
    {
        if (disposed) return;
        try
        {
            // Rearma la notificación antes de procesar para no perder cambios posteriores.
            if (!FindNextPrinterChangeNotification(change, out _, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "FindNextPrinterChangeNotification");
            onChange();
            Register();
        }
        catch (Exception ex)
        {
            if (!disposed) onError(ex);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        registration?.Unregister(null);
        FindClosePrinterChangeNotification(change);
        waitHandle.Dispose();
        printer.Dispose();
    }
}
