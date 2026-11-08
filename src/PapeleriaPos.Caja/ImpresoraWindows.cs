using System.ComponentModel;
using System.Runtime.InteropServices;
using PapeleriaPos.Impresion;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Caja;

/// <summary>
/// Manda el ticket ESC/POS directo a la impresora de Windows (modo RAW), sin pasar por el diseño de página: así sale
/// rápido, con QR y abriendo el cajón.
/// </summary>
public sealed class ImpresoraWindows(string nombre) : IImpresora
{
    public bool MuestraVistaPrevia => false;

    public Task EnviarAsync(byte[] escPos, CancellationToken ct = default) =>
        Task.Run(() => EnviarRaw(nombre, escPos, "Papelería POS"), ct);

    private static void EnviarRaw(string impresora, byte[] datos, string documento)
    {
        if (!OpenPrinter(impresora, out var manejador, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"No se encontró la impresora «{impresora}».");
        try
        {
            // Sin línea o en pausa, Windows acepta el trabajo y lo guarda: la pantalla diría «impreso» y saldría horas después, o nunca.
            if (SinLineaOEnPausa(manejador))
                throw new ErrorAlConectar("La impresora de tickets está sin línea o en pausa en Windows: revisa que esté prendida y conectada.");
            var info = new DocInfo { Nombre = documento, TipoDatos = "RAW" };
            if (!StartDocPrinter(manejador, 1, info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows no pudo mandar el ticket a la impresora: revisa que esté prendida.");
            try
            {
                StartPagePrinter(manejador);
                var puntero = Marshal.AllocCoTaskMem(datos.Length);
                try
                {
                    Marshal.Copy(datos, 0, puntero, datos.Length);
                    if (!WritePrinter(manejador, puntero, datos.Length, out var escritos) || escritos != datos.Length)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "La impresora no recibió el ticket completo.");
                }
                finally
                {
                    Marshal.FreeCoTaskMem(puntero);
                }
                EndPagePrinter(manejador);
            }
            finally
            {
                EndDocPrinter(manejador);
            }
        }
        finally
        {
            ClosePrinter(manejador);
        }
    }

    /// <summary>Lo que dice Windows de la impresora (PRINTER_INFO_2). Si no se puede leer, se manda como siempre.</summary>
    private static bool SinLineaOEnPausa(IntPtr manejador)
    {
        const uint TrabajarSinLinea = 0x400, EnPausa = 0x1, SinLinea = 0x80;
        GetPrinter(manejador, 2, IntPtr.Zero, 0, out var largo);
        if (largo <= 0)
            return false;
        var puntero = Marshal.AllocCoTaskMem(largo);
        try
        {
            if (!GetPrinter(manejador, 2, puntero, largo, out _))
                return false;
            var datos = Marshal.PtrToStructure<InfoImpresora>(puntero);
            return (datos.Atributos & TrabajarSinLinea) != 0 || (datos.Estado & (EnPausa | SinLinea)) != 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(puntero);
        }
    }

    /// <summary>PRINTER_INFO_2W: 13 apuntadores y después sus números, en este orden.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct InfoImpresora
    {
        public IntPtr Servidor, Nombre, Compartida, Puerto, Controlador, Comentario, Lugar, Modo, Separador, Procesador, TipoDatos, Parametros, Seguridad;
        public uint Atributos, Prioridad, PrioridadDeOmision, Inicio, Fin, Estado, Trabajos, Velocidad;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DocInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Nombre;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Archivo;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TipoDatos;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string nombre, out IntPtr manejador, IntPtr opciones);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr manejador);

    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true)]
    private static extern bool GetPrinter(IntPtr manejador, int nivel, IntPtr datos, int largo, out int necesario);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartDocPrinter(IntPtr manejador, int nivel, [In, MarshalAs(UnmanagedType.LPStruct)] DocInfo info);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr manejador);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr manejador);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr manejador);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr manejador, IntPtr datos, int largo, out int escritos);
}
