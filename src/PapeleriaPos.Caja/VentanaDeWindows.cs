using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Caja;

/// <summary>
/// La ventana de la caja como la de eleventa (el dueño, 2026-11-07):
/// <list type="bullet">
/// <item>sin la barra de título de Windows: la barra de arriba de la caja queda pegada al borde. Ocupa la pantalla sin tapar la barra
/// de tareas (el área de trabajo de Windows, y la sigue si cambia), así eleventa y lo demás se siguen abriendo desde ahí;</item>
/// <item>desde arriba solo se minimiza: la × (ya no se ve, pero Alt+F4 también llega aquí) y «Minimizar la caja» del menú del usuario;</item>
/// <item>se cierra desde el corte, con «Cerrar la caja» (<see cref="Cerrar"/>), y pasa por lo que pregunta al cerrar (lo que falta
/// imprimir, el corte en eleventa).</item>
/// </list>
/// Apagar Windows o cerrarse por un error no pasan por aquí: esos sí la cierran.
/// </summary>
internal sealed class VentanaDeWindows : IVentanaDeLaCaja
{
    private readonly Window _ventana;
    private bool _cerrarDeVerdad;

    private VentanaDeWindows(Window ventana) => _ventana = ventana;

    /// <summary>
    /// Se prepara antes de que la ventana se abra y antes que los demás que la escuchan al cerrar: si se cancela aquí, ellos ya no
    /// preguntan nada (revisan <see cref="CancelEventArgs.Cancel"/>).
    /// </summary>
    public static VentanaDeWindows Preparar(Window ventana)
    {
        var v = new VentanaDeWindows(ventana);
        ventana.WindowStyle = WindowStyle.None;
        ventana.ResizeMode = ResizeMode.NoResize;
        ventana.WindowState = WindowState.Normal;
        v.Ocupar();
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea))
                ventana.Dispatcher.BeginInvoke(v.Ocupar);
        };
        // Sin barra de título Windows no deja minimizarla con un clic en su botón de la barra de tareas: se le regresa eso.
        ventana.SourceInitialized += (_, _) => PermitirMinimizar(ventana);
        // Maximizada (doble clic, Windows+↑) se saldría del área de trabajo y taparía la barra de tareas: se queda como está.
        ventana.StateChanged += (_, _) =>
        {
            if (ventana.WindowState == WindowState.Maximized)
            {
                ventana.WindowState = WindowState.Normal;
                v.Ocupar();
            }
        };
        ventana.Closing += v.AlCerrar;
        return v;
    }

    public void Minimizar() => _ventana.Dispatcher.BeginInvoke(() => _ventana.WindowState = WindowState.Minimized);

    public void Cerrar() => _ventana.Dispatcher.BeginInvoke(() =>
    {
        _cerrarDeVerdad = true;
        _ventana.Close();
        // Si quien escucha al cerrar decidió no cerrar (quedaba algo por imprimir), la siguiente × vuelve a minimizar.
        _cerrarDeVerdad = false;
    });

    private void AlCerrar(object? sender, CancelEventArgs e)
    {
        if (_cerrarDeVerdad || Application.Current is App { SeCierraPorUnError: true })
            return;
        e.Cancel = true;
        _ventana.WindowState = WindowState.Minimized;
    }

    /// <summary>Toda el área de trabajo de la pantalla principal: la pantalla menos la barra de tareas.</summary>
    private void Ocupar()
    {
        var area = SystemParameters.WorkArea;
        (_ventana.Left, _ventana.Top, _ventana.Width, _ventana.Height) = (area.Left, area.Top, area.Width, area.Height);
    }

    private const int GWL_STYLE = -16;
    private const int WS_MINIMIZEBOX = 0x00020000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private static void PermitirMinimizar(Window ventana)
    {
        var handle = new WindowInteropHelper(ventana).Handle;
        if (handle != IntPtr.Zero)
            SetWindowLong(handle, GWL_STYLE, GetWindowLong(handle, GWL_STYLE) | WS_MINIMIZEBOX);
    }
}
