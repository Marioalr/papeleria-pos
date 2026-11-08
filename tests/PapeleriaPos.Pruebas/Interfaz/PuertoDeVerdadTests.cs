using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PapeleriaPos.Interfaz;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>Solo en Linux: una terminal virtual (pty) hace del puerto COM.</summary>
public sealed class FactConTerminalVirtualAttribute : FactAttribute
{
    public FactConTerminalVirtualAttribute()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/dev/ptmx"))
            Skip = "La terminal virtual que hace del puerto COM es de Linux; en Windows corren las de mentira.";
    }
}

/// <summary>
/// Una terminal virtual de Linux (pty): lo que se escribe en su lado maestro llega a su lado esclavo («/dev/pts/N»), que se abre
/// como un puerto COM con System.IO.Ports. Así se prueba la lectura de verdad de la caja (<see cref="PuertosDelSistema"/>: abrir,
/// esperar con su plazo, leer lo que llega en pedazos y soltar la lectura al cerrar), no solo el puerto de mentira.
/// </summary>
internal sealed class TerminalVirtual : IDisposable
{
    private const int LecturaEscritura = 2, SinTerminalDeControl = 0x100;

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_openpt(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int grantpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlockpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ptsname_r(int fd, byte[] buf, nint buflen);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, byte[] buf, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    private int _maestro;

    private TerminalVirtual(int maestro, string esclavo) => (_maestro, Esclavo) = (maestro, esclavo);

    /// <summary>El nombre del lado que se abre como puerto: «/dev/pts/7».</summary>
    public string Esclavo { get; }

    public static TerminalVirtual Abrir()
    {
        var maestro = posix_openpt(LecturaEscritura | SinTerminalDeControl);
        if (maestro < 0 || grantpt(maestro) != 0 || unlockpt(maestro) != 0)
            throw new IOException($"No se abrió la terminal virtual (errno {Marshal.GetLastPInvokeError()}).");
        var nombre = new byte[128];
        if (ptsname_r(maestro, nombre, nombre.Length) != 0)
            throw new IOException($"Sin el nombre de la terminal virtual (errno {Marshal.GetLastPInvokeError()}).");
        return new TerminalVirtual(maestro, Encoding.ASCII.GetString(nombre, 0, Array.IndexOf(nombre, (byte)0)));
    }

    /// <summary>Lo que «manda el lector».</summary>
    public void Escribir(string texto)
    {
        var bytes = Encoding.Latin1.GetBytes(texto);
        if (write(_maestro, bytes, bytes.Length) != bytes.Length)
            throw new IOException($"No se escribió en la terminal virtual (errno {Marshal.GetLastPInvokeError()}).");
    }

    public void Dispose()
    {
        if (_maestro > 0)
            close(_maestro);
        _maestro = -1;
    }
}

public sealed class PuertoDeVerdadTests
{
    private static async Task EsperarAsync(Func<bool> condicion, Func<string> que)
    {
        var reloj = Stopwatch.StartNew();
        while (!condicion())
        {
            if (reloj.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException($"No pasó: {que()}.");
            await Task.Delay(10);
        }
    }

    [FactConTerminalVirtual]
    public async Task El_puerto_de_verdad_lee_los_codigos_en_pedazos_con_su_fin_o_su_pausa_y_suelta_la_lectura_al_cerrar()
    {
        using var terminal = TerminalVirtual.Abrir();
        // Una terminal virtual no tiene las líneas DTR y RTS de un puerto: abre igual, sin ellas.
        var ajustes = new AjustesLector(terminal.Esclavo) { Silencio = 80 };
        var lector = new LectorPorPuerto(ajustes, null, new PuertosDelSistema(), reintento: TimeSpan.FromMilliseconds(200));
        var codigos = new ConcurrentQueue<string>();
        lector.Codigo += l => codigos.Enqueue(l.Codigo!);
        lector.Iniciar();
        await EsperarAsync(() => lector.Estado.Situacion == SituacionLector.Listo, () => $"que abra {terminal.Esclavo} ({lector.Estado.Situacion}: {lector.Estado.Detalle})");

        terminal.Escribir("7501234567890\r\n");
        terminal.Escribir("75");
        await Task.Delay(20);
        terminal.Escribir("02\r");
        terminal.Escribir("A-000123");
        await EsperarAsync(() => codigos.Count == 3, () => $"los tres códigos (llegaron {string.Join(", ", codigos)})");

        Assert.Equal(["7501234567890", "7502", "A-000123"], codigos);
        Assert.Equal([FinLeido.Pausa, FinLeido.CR, FinLeido.CRLF], lector.Ultimas.Select(l => l.Fin));

        // Al cerrar la caja, la lectura que esperaba se suelta: el hilo del lector termina.
        var cerrar = lector.DisposeAsync().AsTask();
        Assert.True(await Task.WhenAny(cerrar, Task.Delay(TimeSpan.FromSeconds(5))) == cerrar, "La lectura no se soltó al cerrar.");
    }

    [FactConTerminalVirtual]
    public async Task Con_el_puerto_abierto_por_otro_dice_ocupado_y_si_desaparece_dice_sin_conectar()
    {
        var terminal = TerminalVirtual.Abrir();
        var ajustes = new AjustesLector(terminal.Esclavo);
        await using var primero = new LectorPorPuerto(ajustes, null, new PuertosDelSistema(), reintento: TimeSpan.FromMilliseconds(100));
        primero.Iniciar();
        await EsperarAsync(() => primero.Estado.Situacion == SituacionLector.Listo, () => $"que abra el primero ({primero.Estado.Situacion}: {primero.Estado.Detalle})");

        // Otro programa con el mismo puerto (como eleventa y la caja): el segundo dice ocupado y lo sigue intentando.
        await using (var segundo = new LectorPorPuerto(ajustes, null, new PuertosDelSistema(), reintento: TimeSpan.FromMilliseconds(100)))
        {
            segundo.Iniciar();
            await EsperarAsync(() => segundo.Estado.Situacion == SituacionLector.Ocupado, () => $"ocupado ({segundo.Estado.Situacion}: {segundo.Estado.Detalle})");
            Assert.Equal("Lector ocupado: ¿eleventa abierto?", segundo.Estado.Chip);
        }

        // Desaparece el puerto (como un lector USB que se desconecta): el primero lo nota, sin girar, y lo busca.
        terminal.Dispose();
        await EsperarAsync(() => primero.Estado.Situacion == SituacionLector.SinConectar, () => $"sin conectar ({primero.Estado.Situacion}: {primero.Estado.Detalle})");
    }
}
