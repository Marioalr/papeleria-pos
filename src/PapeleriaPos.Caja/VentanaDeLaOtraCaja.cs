using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PapeleriaPos.Caja;

/// <summary>
/// Se abrió la caja con otra ya abierta: se trae al frente la que está, para que parezca que abrió. Si esa no contesta (se congeló, o sigue
/// viva sin ventana), <see cref="App"/> lo dice y ofrece <see cref="Cerrarla"/>: si no, la cajera se queda sin caja y sin saber por qué.
/// </summary>
internal static class VentanaDeLaOtraCaja
{
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr ventana);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr ventana, int como);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr ventana);

    /// <summary>Si una contestó y quedó al frente. Sin ventana todavía (una PC lenta, o terminando de cerrarse) o congelada, no.</summary>
    public static bool AlFrente()
    {
        var alFrente = false;
        foreach (var otra in LasOtras())
        {
            using (otra)
            {
                if (alFrente || otra.MainWindowHandle == IntPtr.Zero || !otra.Responding)
                    continue;
                if (IsIconic(otra.MainWindowHandle))
                    ShowWindow(otra.MainWindowHandle, SwRestore);
                SetForegroundWindow(otra.MainWindowHandle);
                alFrente = true;
            }
        }
        return alFrente;
    }

    /// <summary>Cierra a la fuerza las otras de esta instalación, con su WebView2. Lo que no se deje, se nota en que el candado sigue tomado.</summary>
    public static void Cerrarla()
    {
        foreach (var otra in LasOtras())
        {
            using (otra)
            {
                try
                {
                    otra.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or AggregateException)
                {
                    // Ya se había cerrado, o Windows no dejó.
                }
            }
        }
    }

    /// <summary>
    /// Las otras cajas de este usuario y de este mismo programa: la de la prueba, instalada aparte, se llama igual pero no es esta.
    /// </summary>
    private static IEnumerable<Process> LasOtras()
    {
        using var esta = Process.GetCurrentProcess();
        foreach (var otra in Process.GetProcessesByName(esta.ProcessName))
        {
            if (otra.Id != esta.Id && otra.SessionId == esta.SessionId && DeEsteMismoPrograma(otra))
                yield return otra;
            else
                otra.Dispose();
        }
    }

    private static bool DeEsteMismoPrograma(Process otra)
    {
        try
        {
            return string.Equals(otra.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Ya se cerró, o es de otro usuario.
            return false;
        }
    }
}
