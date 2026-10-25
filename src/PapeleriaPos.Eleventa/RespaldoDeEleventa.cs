using System.Diagnostics;
using System.Globalization;

namespace PapeleriaPos.Eleventa;

/// <summary>
/// Corre gbak. En la tienda, el <c>gbak.exe</c> de Firebird 2.0 (<see cref="EjecutorGbak"/>); en las pruebas, uno de
/// mentiras: nunca contra nada real.
/// </summary>
public interface IEjecutorGbak
{
    /// <param name="entorno">Variables de entorno para gbak: el usuario y la clave van ahí, no en la línea de comandos.</param>
    /// <returns>El código con que terminó (0 si respaldó) y lo que escribió.</returns>
    Task<(int Codigo, string Salida)> EjecutarAsync(string gbak, IReadOnlyList<string> argumentos, IReadOnlyDictionary<string, string> entorno,
        CancellationToken ct);
}

/// <summary>El gbak de verdad: un proceso sin ventana. Si se cancela (tardó demasiado), se detiene.</summary>
public sealed class EjecutorGbak : IEjecutorGbak
{
    public async Task<(int Codigo, string Salida)> EjecutarAsync(string gbak, IReadOnlyList<string> argumentos, IReadOnlyDictionary<string, string> entorno,
        CancellationToken ct)
    {
        var inicio = new ProcessStartInfo(gbak)
        {
            // gbak busca la biblioteca de Firebird (fbclient.dll o gds32.dll) junto a él.
            WorkingDirectory = Path.GetDirectoryName(gbak) ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argumento in argumentos)
            inicio.ArgumentList.Add(argumento);
        foreach (var (clave, valor) in entorno)
            inicio.Environment[clave] = valor;

        using var proceso = Process.Start(inicio) ?? throw new InvalidOperationException($"No arrancó {gbak}.");
        var salida = proceso.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errores = proceso.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await proceso.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proceso.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Ya había terminado.
            }
            throw;
        }
        return (proceso.ExitCode, $"{await errores}\n{await salida}".Trim());
    }
}

/// <summary>Dónde y con qué respalda el puente a eleventa.</summary>
/// <param name="Gbak">La ruta de <c>gbak.exe</c> en la caja 2 (la de Firebird 2.0, como el de la tienda). Vacía: no se respalda.</param>
/// <param name="Carpeta">En el disco de la caja 2 y fuera de OneDrive.</param>
/// <param name="Dias">Cuántos días de respaldos se guardan.</param>
public sealed record OpcionesRespaldoEleventa(string? Gbak, string Carpeta, int Dias = 5);

/// <summary>Cómo quedó el respaldo del día: el archivo, o por qué no se pudo (dicho para la tienda).</summary>
public sealed record RespaldoHecho(string? Archivo, string? Error)
{
    public bool Listo => Archivo is not null;
}

/// <summary>
/// Los respaldos de eleventa (docs/migracion-eleventa.md, «Los respaldos de eleventa»): antes de lo primero que el puente
/// escribe en eleventa cada día, su propio respaldo de la base de eleventa con <c>gbak</c>. Con Firebird 2.0 no hay
/// respaldo por la API de servicios que llegue a la caja 2 (eso es de 2.5), pero gbak, con la base de la caja principal
/// («servidor:ruta»), escribe el respaldo en el disco de la caja 2. Va a una carpeta fuera de OneDrive, un archivo por
/// día (<c>eleventa-AAAA-MM-DD.fbk</c>, primero como <c>.tmp</c>: un respaldo a medias nunca parece bueno), y se guardan
/// unos días. Lo usa la bandeja (<c>EnvioVentasEleventa</c>): si no se pudo, por omisión no manda nada a eleventa y la
/// pantalla de la caja 2 dice por qué. Solo del piloto: se quita al dejar eleventa.
/// </summary>
public sealed class RespaldoDeEleventa(OpcionesRespaldoEleventa opciones, IEjecutorGbak ejecutor)
{
    /// <summary>Lo más que se espera a gbak (la base de la tienda, unos 90 MB por la red, tarda uno o dos minutos).</summary>
    public static readonly TimeSpan Limite = TimeSpan.FromMinutes(10);

    private const string Prefijo = "eleventa-";
    private const string Extension = ".fbk";

    /// <summary>
    /// Junto a cada respaldo, de qué base es (la PC, el puerto y la ruta; sin la clave): <c>eleventa-AAAA-MM-DD.fbk.origen</c>. El
    /// piloto alfa (2026-10-25): el ensayo escribe en una copia de la base y después se vuelve a la real, a veces el mismo día; el
    /// respaldo del día de la copia no es el de la real, y antes de escribirle a la real se respalda la real.
    /// </summary>
    private const string Origen = ".origen";

    public OpcionesRespaldoEleventa Opciones => opciones;

    /// <summary>Si hay con qué respaldar (la ruta de gbak).</summary>
    public bool Configurado => !string.IsNullOrWhiteSpace(opciones.Gbak);

    public string Archivo(DateOnly dia) => Path.Combine(opciones.Carpeta, $"{Prefijo}{dia:yyyy-MM-dd}{Extension}");

    /// <summary>
    /// Si ya está el respaldo de ese día (un archivo completo: el de a medias se queda como <c>.tmp</c>) y, si se dice la base, si es
    /// de esa base (el de otra, como la copia del ensayo, no cuenta). Uno sin su <c>.origen</c> (de una versión de antes) cuenta.
    /// </summary>
    public bool HayDelDia(DateOnly dia, ServidorEleventa? de = null)
    {
        var archivo = new FileInfo(Archivo(dia));
        if (!archivo.Exists || archivo.Length == 0)
            return false;
        if (de is null)
            return true;
        try
        {
            var origen = archivo.FullName + Origen;
            return !File.Exists(origen) || File.ReadAllText(origen).Trim() == de.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// El respaldo de ese día, si no está: gbak con la base de eleventa por la red, primero a un <c>.tmp</c> que se renombra
    /// si terminó bien; después se borran los de hace más de <see cref="OpcionesRespaldoEleventa.Dias"/> días. Nunca truena:
    /// si no se pudo, dice por qué.
    /// </summary>
    public async Task<RespaldoHecho> HacerAsync(ServidorEleventa servidor, DateOnly dia, CancellationToken ct = default)
    {
        if (HayDelDia(dia, servidor))
            return new RespaldoHecho(Archivo(dia), null);
        if (!Configurado)
            return new RespaldoHecho(null, "Falta la ruta de gbak.exe en la configuración (Eleventa:Gbak).");
        if (EnOneDrive(opciones.Carpeta))
            return new RespaldoHecho(null, $"La carpeta de los respaldos de eleventa está en OneDrive ({opciones.Carpeta}): ponla fuera (Eleventa:CarpetaRespaldos).");
        if (!File.Exists(opciones.Gbak))
            return new RespaldoHecho(null, $"No está gbak.exe en {opciones.Gbak} (Eleventa:Gbak).");

        var destino = Archivo(dia);
        var temporal = destino + ".tmp";
        try
        {
            Directory.CreateDirectory(opciones.Carpeta);
            File.Delete(temporal);
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(Limite);
            (int Codigo, string Salida) gbak;
            try
            {
                gbak = await ejecutor.EjecutarAsync(opciones.Gbak!, Argumentos(servidor, temporal), Entorno(servidor), limite.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new RespaldoHecho(null, $"gbak no terminó en {Limite.TotalMinutes:0} minutos.");
            }
            var hecho = new FileInfo(temporal);
            if (gbak.Codigo != 0 || !hecho.Exists || hecho.Length == 0)
                return new RespaldoHecho(null, $"gbak no pudo respaldar eleventa (código {gbak.Codigo}): {Ultimas(gbak.Salida)}");
            // De qué base es, en este orden: si se corta a la mitad, el respaldo queda sin su .origen (cuenta como de esta base) y
            // nunca con el de otra.
            AnotarOrigen(destino, null);
            File.Move(temporal, destino, overwrite: true);
            AnotarOrigen(destino, servidor);
            Limpiar(dia);
            return new RespaldoHecho(destino, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new RespaldoHecho(null, $"No se pudo respaldar eleventa en {opciones.Carpeta}: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(temporal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Se borra en el siguiente.
            }
        }
    }

    /// <summary>
    /// El <c>.origen</c> de un respaldo: de qué base es (vacío: se quita). Si no se puede escribir, el respaldo sigue bien: sin él
    /// cuenta como de cualquier base, como los de antes; si el de otra base no se pudo quitar, el siguiente lo pisa.
    /// </summary>
    private static void AnotarOrigen(string respaldo, ServidorEleventa? servidor)
    {
        try
        {
            if (servidor is null)
                File.Delete(respaldo + Origen);
            else
                File.WriteAllText(respaldo + Origen, servidor.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ver arriba.
        }
    }

    /// <summary>
    /// gbak en modo respaldo (<c>-b</c>), sin limpiar la base mientras respalda (<c>-g</c>: eleventa está trabajando), con la
    /// base como la escribe Firebird 2.0 («servidor:ruta», o «servidor/puerto:ruta») y el archivo en la caja 2.
    /// </summary>
    public static IReadOnlyList<string> Argumentos(ServidorEleventa servidor, string archivo) =>
        ["-b", "-g", servidor.Puerto == 3050 ? $"{servidor.Servidor}:{servidor.Base}" : $"{servidor.Servidor}/{servidor.Puerto}:{servidor.Base}", archivo];

    /// <summary>El usuario y la clave de Firebird, como los toma gbak del entorno: así la clave no se ve en la lista de procesos.</summary>
    public static IReadOnlyDictionary<string, string> Entorno(ServidorEleventa servidor) =>
        new Dictionary<string, string> { ["ISC_USER"] = servidor.Usuario, ["ISC_PASSWORD"] = servidor.Clave };

    /// <summary>Borra los respaldos (y los <c>.tmp</c>) de hace más de los días que se guardan; nada más de esa carpeta.</summary>
    public int Limpiar(DateOnly hoy)
    {
        if (!Directory.Exists(opciones.Carpeta))
            return 0;
        var borrados = 0;
        foreach (var archivo in Directory.EnumerateFiles(opciones.Carpeta, $"{Prefijo}*"))
        {
            var nombre = Path.GetFileName(archivo);
            var fecha = nombre.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? nombre[Prefijo.Length..^Extension.Length]
                : nombre.EndsWith(Extension + ".tmp", StringComparison.OrdinalIgnoreCase) ? nombre[Prefijo.Length..^(Extension.Length + 4)]
                : nombre.EndsWith(Extension + Origen, StringComparison.OrdinalIgnoreCase) ? nombre[Prefijo.Length..^(Extension.Length + Origen.Length)]
                : null;
            if (fecha is null || !DateOnly.TryParseExact(fecha, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dia)
                || dia > hoy.AddDays(-Math.Max(1, opciones.Dias)))
                continue;
            try
            {
                File.Delete(archivo);
                borrados++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Se intenta en el siguiente.
            }
        }
        return borrados;
    }

    /// <summary>
    /// Si la carpeta está dentro de OneDrive (la de la cuenta o una que se llame así): los respaldos son datos de la tienda y
    /// no se suben a la nube.
    /// </summary>
    public static bool EnOneDrive(string carpeta)
    {
        var partes = carpeta.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (partes.Any(p => p.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)))
            return true;
        var completa = Path.GetFullPath(carpeta);
        return new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .Any(raiz => raiz is { Length: > 0 } && completa.StartsWith(Path.GetFullPath(raiz), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Lo último que dijo gbak (su error va al final).</summary>
    private static string Ultimas(string salida)
    {
        var renglones = salida.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return renglones.Length == 0 ? "no dijo nada" : string.Join(" ", renglones.TakeLast(3));
    }
}
