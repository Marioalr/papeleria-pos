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

    public Task ImprimirAsync(TicketDto ticket, bool abrirCajon, CancellationToken ct = default) =>
        Task.Run(() => EnviarRaw(nombre, TicketEscPos.Generar(ticket, abrirCajon), $"Ticket {ticket.Folio}"), ct);

    private static void EnviarRaw(string impresora, byte[] datos, string documento)
    {
        if (!OpenPrinter(impresora, out var manejador, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"No se encontró la impresora «{impresora}».");
        try
        {
            var info = new DocInfo { Nombre = documento, TipoDatos = "RAW" };
            if (!StartDocPrinter(manejador, 1, info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
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
