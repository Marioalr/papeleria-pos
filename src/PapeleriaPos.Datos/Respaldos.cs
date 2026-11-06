using System.Globalization;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;

namespace PapeleriaPos.Datos;

/// <summary>
/// Copias de la base en la caja principal: una al día y otra con cada corte Z, con los últimos días de historia. Se
/// hacen con la copia en línea de SQLite, así que la venta no se detiene.
/// </summary>
public static class Respaldos
{
    private const string Prefijo = "pos-";

    /// <summary>Copia la base a <c>pos-AAAA-MM-DD[-etiqueta].db</c> y devuelve la ruta.</summary>
    /// <remarks>
    /// Si la copia falla, su temporal (y su -wal o -shm) se borra ahí mismo (revisión del PR #133): con el nombre por minuto de
    /// «Respaldar ahora» nadie lo volvía a borrar, y la limpieza tampoco. Antes de darle el nombre bueno se le hace la revisión completa
    /// (auditoría, 2026-11-04): la copia lleva lo que tenga la base, también una página dañada o un índice que no cuadra, y con los días
    /// la limpieza se llevaría los buenos. Corre sobre la copia: no detiene la venta. Si no pasa, <see cref="CopiaDanadaException"/>.
    /// </remarks>
    public static string Hacer(string cadenaConexion, string carpeta, DateTime fechaLocal, string? etiqueta = null)
    {
        Directory.CreateDirectory(carpeta);
        var destino = Path.Combine(carpeta, $"{Prefijo}{fechaLocal:yyyy-MM-dd}{(etiqueta is null ? "" : "-" + etiqueta)}.db");
        var temporal = destino + Temporal;
        BorrarTemporal(temporal);
        try
        {
            using (var origen = new SqliteConnection(cadenaConexion))
            using (var copia = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporal, Pooling = false }.ToString()))
            {
                origen.Open();
                copia.Open();
                // También aquí synchronous=FULL: la copia queda en el disco antes de renombrarla (entrega 10).
                BaseDeDatos.Ajustar(origen);
                BaseDeDatos.Ajustar(copia);
                origen.BackupDatabase(copia);
            }
            // Sin el pool: con la conexión abierta, Windows no dejaría renombrar el archivo.
            var revision = BaseDeDatos.RevisionCompleta(new SqliteConnectionStringBuilder { DataSource = temporal, Pooling = false }.ToString());
            if (BaseDeDatos.NoSePudoRevisar(revision))
                throw new IOException(revision[0]);
            if (!BaseDeDatos.EstaSana(revision))
                throw new CopiaDanadaException($"La copia no pasó la revisión: {revision[0]}");
            // Se escribe aparte y se renombra: una copia a medias nunca queda con el nombre bueno.
            Renombrar(temporal, destino);
            return destino;
        }
        finally
        {
            // Ya renombrada no queda nada; si falló, no se queda a medias ocupando el disco.
            BorrarTemporal(temporal);
        }
    }

    /// <summary>Unos intentos, como al restaurar: el antivirus puede tener abierto un momento el archivo recién escrito.</summary>
    private static void Renombrar(string temporal, string destino)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                File.Move(temporal, destino, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && intento < 5 && File.Exists(temporal))
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }
    }

    /// <summary>Lo que lleva el archivo mientras se escribe: <c>pos-….db.tmp</c>, con su -wal y su -shm si los hace SQLite.</summary>
    private const string Temporal = ".tmp";

    /// <summary>El temporal y lo que SQLite deja junto a él; lo que no se puede borrar (ocupado), se queda para la limpieza.</summary>
    private static void BorrarTemporal(string temporal)
    {
        foreach (var archivo in new[] { temporal, temporal + "-wal", temporal + "-shm", temporal + "-journal" })
        {
            try
            {
                File.Delete(archivo);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Lo borra la limpieza al día siguiente.
            }
        }
    }

    /// <summary>La copia más reciente (la diaria o la de un corte Z), o nada si no hay.</summary>
    public static string? Ultima(string carpeta) =>
        Directory.Exists(carpeta)
            ? Directory.EnumerateFiles(carpeta, Prefijo + "*.db").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;

    /// <summary>Si ya está la copia diaria (sin etiqueta) de esa fecha.</summary>
    public static bool HayDelDia(string carpeta, DateOnly fecha) =>
        File.Exists(Path.Combine(carpeta, $"{Prefijo}{fecha:yyyy-MM-dd}.db"));

    /// <summary>Cuántas copias se quedan siempre, sean de cuando sean: con la fecha de la PC adelantada, si no, se irían todas (auditoría, 2026-11-04).</summary>
    private const int SiempreSeQuedan = 5;

    /// <summary>
    /// Borra las copias de hace <paramref name="dias"/> días o más, salvo las más recientes: también se limpia cuando la copia de hoy
    /// falla, y nunca se queda sin ninguna. Devuelve cuántas borró. También los temporales que se quedaron
    /// de un día anterior (una copia que se cortó: un apagón a media copia), que ya nadie va a terminar (revisión del PR #133).
    /// </summary>
    public static int Limpiar(string carpeta, DateOnly hoy, int dias) => Limpiar(carpeta, hoy, dias, File.Delete);

    /// <param name="borrar">Las pruebas lo cambian para simular un archivo ocupado: los demás se borran, y al final se lanza ese error.</param>
    public static int Limpiar(string carpeta, DateOnly hoy, int dias, Action<string> borrar)
    {
        if (!Directory.Exists(carpeta))
            return 0;
        var limite = hoy.AddDays(-dias);
        var borradas = 0;
        Exception? ocupado = null;
        // Del nombre: pos-AAAA-MM-DD, y lo que siga, se ordena igual que su fecha.
        var recientes = Directory.EnumerateFiles(carpeta, Prefijo + "*.db").Where(a => FechaDe(a) is not null)
            .OrderByDescending(a => FechaDe(a)).ThenByDescending(a => Path.GetFileName(a), StringComparer.Ordinal).Take(SiempreSeQuedan).ToHashSet();
        foreach (var archivo in Directory.EnumerateFiles(carpeta, Prefijo + "*.db"))
        {
            if (FechaDe(archivo) is { } fecha && fecha <= limite && !recientes.Contains(archivo))
            {
                try
                {
                    borrar(archivo);
                    borradas++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ocupado ??= ex;
                }
            }
        }
        foreach (var archivo in Directory.EnumerateFiles(carpeta, Prefijo + "*.db" + Temporal + "*"))
        {
            if (FechaDe(archivo) is { } fecha && (fecha < hoy || fecha <= limite))
            {
                try
                {
                    borrar(archivo);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ocupado ??= ex;
                }
            }
        }
        if (ocupado is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ocupado);
        return borradas;
    }

    /// <summary>
    /// Los respaldos de la carpeta (<c>pos-….db</c>, sin los temporales), el más reciente primero: para elegir cuál restaurar
    /// (Configuración › Respaldos, 2026-10-23). Su hora, la de cuando se terminó de escribir (la del nombre solo la llevan el del
    /// corte Z y el de «Respaldar ahora»).
    /// </summary>
    public static IReadOnlyList<RespaldoGuardadoDto> Listar(string carpeta)
    {
        if (!Directory.Exists(carpeta))
            return [];
        return Directory.EnumerateFiles(carpeta, Prefijo + "*.db")
            .Select(ruta => new FileInfo(ruta))
            .Where(archivo => FechaDe(archivo.Name) is not null)
            .OrderByDescending(archivo => archivo.LastWriteTimeUtc)
            .Select(archivo => new RespaldoGuardadoDto(archivo.Name, TipoDe(archivo.Name), HoraMexico.ALocal(archivo.LastWriteTimeUtc), archivo.Length))
            .ToList();
    }

    /// <summary>
    /// De qué es, por su nombre: <c>pos-AAAA-MM-DD.db</c> el del día, <c>…-z-HHmm.db</c> el de un corte Z, <c>…-ahora-HHmm.db</c> el
    /// de «Respaldar ahora» y <c>…-restaurado-HHmm.db</c> el de al arrancar con un respaldo restaurado (<see cref="Hacer"/>).
    /// </summary>
    public static TipoRespaldo TipoDe(string archivo)
    {
        var nombre = Path.GetFileNameWithoutExtension(archivo);
        if (FechaDe(archivo) is null)
            return TipoRespaldo.Otro;
        var etiqueta = nombre.Length > Prefijo.Length + 10 ? nombre[(Prefijo.Length + 10)..] : "";
        return etiqueta switch
        {
            "" => TipoRespaldo.Diario,
            _ when System.Text.RegularExpressions.Regex.IsMatch(etiqueta, @"^-z-\d{4}$") => TipoRespaldo.CorteZ,
            _ when System.Text.RegularExpressions.Regex.IsMatch(etiqueta, @"^-ahora-\d{4}$") => TipoRespaldo.Ahora,
            _ when System.Text.RegularExpressions.Regex.IsMatch(etiqueta, @"^-restaurado-\d{4}$") => TipoRespaldo.Restaurado,
            _ => TipoRespaldo.Otro,
        };
    }

    /// <summary>
    /// Un respaldo de la carpeta por su nombre, tal como lo da <see cref="Listar"/>: solo el nombre (nada de otra carpeta), que
    /// exista y sea un respaldo. Si no, <see cref="InvalidOperationException"/> con el porqué.
    /// </summary>
    public static string Ruta(string carpeta, string archivo)
    {
        if (string.IsNullOrWhiteSpace(archivo) || archivo != Path.GetFileName(archivo) || archivo.Contains("..", StringComparison.Ordinal)
            || !archivo.StartsWith(Prefijo, StringComparison.OrdinalIgnoreCase) || !archivo.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            || FechaDe(archivo) is null)
            throw new InvalidOperationException($"«{archivo}» no es un respaldo de la carpeta de respaldos.");
        var ruta = Path.Combine(carpeta, archivo);
        return File.Exists(ruta) ? ruta : throw new InvalidOperationException($"Ya no está el respaldo {archivo} en {carpeta}.");
    }

    /// <summary>La fecha del nombre (<c>pos-AAAA-MM-DD…</c>); vacía si no la tiene.</summary>
    private static DateOnly? FechaDe(string archivo)
    {
        var nombre = Path.GetFileName(archivo);
        return nombre.Length >= Prefijo.Length + 10
            && DateOnly.TryParseExact(nombre.AsSpan(Prefijo.Length, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha)
            ? fecha
            : null;
    }
}

/// <summary>
/// La copia salió, pero no pasó la revisión completa: la base que se copió está dañada (<see cref="Respaldos.Hacer"/>). No queda como
/// respaldo, y mientras siga así no se borra ninguno de los que hay.
/// </summary>
public sealed class CopiaDanadaException(string mensaje) : IOException(mensaje);
