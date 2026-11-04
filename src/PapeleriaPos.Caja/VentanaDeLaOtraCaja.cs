using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PapeleriaPos.Caja;

/// <summary>Se abrió la caja con otra ya abierta: se trae al frente la que está, para que parezca que abrió. Si no se puede, nada.</summary>
internal static class VentanaDeLaOtraCaja
{
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr ventana);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr ventana, int como);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr ventana);

    public static void AlFrente()
    {
        using var esta = Process.GetCurrentProcess();
        foreach (var otra in Process.GetProcessesByName(esta.ProcessName))
        {
            using (otra)
            {
                // La ventana puede no estar todavía (una PC lenta): entonces sale sola al terminar de abrir.
                if (otra.Id == esta.Id || otra.MainWindowHandle == IntPtr.Zero)
                    continue;
                if (IsIconic(otra.MainWindowHandle))
                    ShowWindow(otra.MainWindowHandle, SwRestore);
                SetForegroundWindow(otra.MainWindowHandle);
                return;
            }
        }
    }
}
