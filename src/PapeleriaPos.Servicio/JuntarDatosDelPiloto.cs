using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Servicio;

/// <summary>Los días que se juntaron y el primero del piloto (el primer día que la caja 2 mandó algo a eleventa).</summary>
public sealed record PeriodoDelPiloto(DateOnly Desde, DateOnly Hasta, DateOnly? PrimerDia, int Envios, int Bitacora);

/// <summary>
/// Lo que junta <c>instalacion\juntar-datos-piloto.ps1</c> de la base de la caja 2 (el piloto alfa, 2026-10-25: «recopilar datos
/// en un entorno real»): la bandeja hacia eleventa (<c>EnviosAEleventa</c>) y la bitácora de esos días en CSV, para abrirlos en
/// Excel, la copia de la base sin secretos y la configuración de esa PC (<c>servicio.json</c>, <c>caja.json</c>) sin secretos. Lo
/// corre el mismo programa del servicio, sin arrancarlo y sin abrir la base que usa:
/// <code>
/// PapeleriaPos.Servicio.exe datos-del-piloto --copia &lt;un respaldo: pos-….db&gt; --salida &lt;carpeta&gt;
///     [--desde AAAA-MM-DD] [--hasta AAAA-MM-DD] [--base-sin-secretos &lt;pos-….db para el .zip&gt;]
///     [--configuracion &lt;servicio.json&gt;] [--caja &lt;caja.json&gt;]
/// </code>
/// Sin <c>--desde</c>, desde el primer día del piloto (lo primero de la bandeja); sin <c>--hasta</c>, hasta hoy. Deja en la
/// carpeta <c>periodo.txt</c> con los días que tomó, para que el script copie los registros de esos mismos días.
/// </summary>
public static class JuntarDatosDelPiloto
{
    public const string Verbo = "datos-del-piloto";

    /// <summary>Cuántos días se toman si no se dicen y la bandeja está vacía (el piloto no ha empezado).</summary>
    public const int DiasSinPiloto = 30;

    /// <summary>
    /// Desde la línea de órdenes. Los mensajes van sin acentos: así se leen igual en la consola de Windows PowerShell 5.1, que es
    /// donde los lee el script. 0 si salió bien, 1 si algo no se pudo, 2 si se le pidió la base en uso.
    /// </summary>
    public static async Task<int> CorrerAsync(IReadOnlyList<string> argumentos, TextWriter salida)
    {
        string? Valor(string nombre)
        {
            for (var i = 0; i < argumentos.Count - 1; i++)
                if (string.Equals(argumentos[i], nombre, StringComparison.OrdinalIgnoreCase))
                    return argumentos[i + 1];
            return null;
        }

        if (Valor("--copia") is not { Length: > 0 } copia || Valor("--salida") is not { Length: > 0 } carpeta)
        {
            await salida.WriteLineAsync($"Uso: PapeleriaPos.Servicio.exe {Verbo} --copia <respaldo pos-....db> --salida <carpeta> [--desde AAAA-MM-DD] "
                + "[--hasta AAAA-MM-DD] [--base-sin-secretos <pos-....db para el .zip>] [--configuracion <servicio.json>] [--caja <caja.json>]");
            return 1;
        }
        if (EsLaBaseEnUso(copia))
        {
            await salida.WriteLineAsync($"{copia} es la base que usa el servicio: se lee solo una copia (un respaldo). No se abrio.");
            return 2;
        }
        DateOnly? Fecha(string nombre) =>
            Valor(nombre) is { Length: > 0 } texto
                ? DateOnly.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha)
                    ? fecha
                    : throw new FormatException($"{nombre} va como AAAA-MM-DD (por ejemplo 2026-10-31), y llego «{texto}».")
                : null;

        var bien = true;
        Directory.CreateDirectory(carpeta);
        try
        {
            var hoy = DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));
            var periodo = await ExportarAsync(copia, Fecha("--desde"), Fecha("--hasta") ?? hoy, carpeta);
            await salida.WriteLineAsync($"Del {periodo.Desde:yyyy-MM-dd} al {periodo.Hasta:yyyy-MM-dd}: {periodo.Envios} renglones de la bandeja hacia eleventa "
                + $"y {periodo.Bitacora} de la bitacora."
                + (periodo.PrimerDia is { } primero ? $" El piloto empezo el {primero:yyyy-MM-dd}." : " La bandeja hacia eleventa esta vacia."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or Microsoft.Data.Sqlite.SqliteException
                                       or InvalidOperationException)
        {
            await salida.WriteLineAsync($"No se pudieron sacar los CSV de la copia de la base: {ex.Message}");
            bien = false;
        }
        if (Valor("--base-sin-secretos") is { Length: > 0 } paraElZip)
        {
            try
            {
                await CopiaSinSecretosAsync(copia, paraElZip);
                await salida.WriteLineAsync($"{Path.GetFileName(paraElZip)}: la copia de la base sin los PIN ni las llaves de las cajas.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
            {
                // Sin ella, el .zip va sin la base: nunca una copia con secretos.
                await salida.WriteLineAsync($"No se pudo dejar la copia de la base sin secretos ({ex.Message}): no va en el .zip.");
                bien = false;
            }
        }
        foreach (var (opcion, nombre) in new[] { ("--configuracion", "servicio.json"), ("--caja", "caja.json") })
        {
            if (Valor(opcion) is not { Length: > 0 } ruta)
                continue;
            if (!File.Exists(ruta))
            {
                await salida.WriteLineAsync($"No esta {ruta}: no se copio.");
                continue;
            }
            try
            {
                await File.WriteAllTextAsync(Path.Combine(carpeta, nombre), SinSecretos(await File.ReadAllTextAsync(ruta)), new UTF8Encoding(false));
                await salida.WriteLineAsync($"{nombre}: copiado sin secretos.");
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Lo que no se pudo leer no se copia tal cual (podría traer un secreto): solo se dice.
                await File.WriteAllTextAsync(Path.Combine(carpeta, nombre + ".txt"), $"No se pudo leer {ruta}: {ex.Message}", new UTF8Encoding(false));
                await salida.WriteLineAsync($"{nombre}: no se pudo leer ({ex.Message}); no se copio.");
                bien = false;
            }
        }
        return bien ? 0 : 1;
    }

    /// <summary>
    /// Si es la base que usa el servicio de esta PC (la de <c>C:\ProgramData\PapeleriaPos</c>), o una base en uso por el servicio
    /// (tiene junto su marca <c>.en-uso</c>, la de la base contra apagones): esa no se abre.
    /// </summary>
    public static bool EsLaBaseEnUso(string ruta)
    {
        var completa = Path.GetFullPath(ruta);
        var enUso = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PapeleriaPos", "pos.db");
        return string.Equals(completa, Path.GetFullPath(enUso), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
               || File.Exists(completa + ".en-uso");
    }

    /// <summary>
    /// Los CSV de esos días: <c>envios-a-eleventa.csv</c> (lo creado en esos días, y lo que sigue pendiente o por revisar aunque sea
    /// de antes) y <c>bitacora.csv</c>, en UTF-8 para Excel; y <c>periodo.txt</c>. Solo lee la copia.
    /// </summary>
    /// <param name="desde">Vacío: desde el primer día del piloto (o los últimos <see cref="DiasSinPiloto"/> días si no ha empezado).</param>
    public static async Task<PeriodoDelPiloto> ExportarAsync(string copia, DateOnly? desde, DateOnly hasta, string carpeta, CancellationToken ct = default)
    {
        if (!File.Exists(copia))
            throw new IOException($"No está la copia de la base {copia}.");
        if (EsLaBaseEnUso(copia))
            throw new InvalidOperationException($"{copia} es la base que usa el servicio: se lee solo una copia.");
        Directory.CreateDirectory(carpeta);
        // Sin pool: al terminar se suelta el archivo (el script lo mete al .zip enseguida).
        await using var db = new PosDbContext(BaseDeDatos.Opciones(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = copia, Pooling = false }.ToString()));

        var primerEnvio = await db.EnviosAEleventa.AsNoTracking().MinAsync(e => (DateTime?)e.CreadoUtc, ct);
        DateOnly? primerDia = primerEnvio is { } p ? DateOnly.FromDateTime(HoraMexico.ALocal(p)) : null;
        var inicio = desde ?? primerDia ?? hasta.AddDays(1 - DiasSinPiloto);
        if (inicio > hasta)
            inicio = hasta;
        var desdeUtc = HoraMexico.AUtc(inicio.ToDateTime(TimeOnly.MinValue));
        var hastaUtc = HoraMexico.AUtc(hasta.AddDays(1).ToDateTime(TimeOnly.MinValue));

        var envios = await EnviosAsync(db, desdeUtc, hastaUtc, ct);
        await EscribirCsvAsync(Path.Combine(carpeta, "envios-a-eleventa.csv"), envios, ct);
        var bitacora = await BitacoraAsync(db, desdeUtc, hastaUtc, ct);
        await EscribirCsvAsync(Path.Combine(carpeta, "bitacora.csv"), bitacora, ct);
        var periodo = new PeriodoDelPiloto(inicio, hasta, primerDia, envios.Count - 1, bitacora.Count - 1);
        await File.WriteAllTextAsync(Path.Combine(carpeta, "periodo.txt"),
            $"desde={periodo.Desde:yyyy-MM-dd}\nhasta={periodo.Hasta:yyyy-MM-dd}\nprimer-dia={periodo.PrimerDia:yyyy-MM-dd}\n", new UTF8Encoding(false), ct);
        return periodo;
    }

    /// <summary>La bandeja hacia eleventa: con encabezados, un renglón por envío, del más viejo al más nuevo.</summary>
    private static async Task<List<string[]>> EnviosAsync(PosDbContext db, DateTime desdeUtc, DateTime hastaUtc, CancellationToken ct)
    {
        var abiertos = new[] { EstadoEnvioEleventa.Pendiente, EstadoEnvioEleventa.PorRevisar };
        var envios = await db.EnviosAEleventa.AsNoTracking()
            .Where(e => (e.CreadoUtc >= desdeUtc && e.CreadoUtc < hastaUtc) || abiertos.Contains(e.Estado))
            .OrderBy(e => e.CreadoUtc).ToListAsync(ct);
        var ventaIds = envios.Where(e => e.VentaId is not null).Select(e => e.VentaId!.Value).Distinct().ToList();
        var ventas = await db.Ventas.AsNoTracking().Where(v => ventaIds.Contains(v.Id))
            .Select(v => new { v.Id, v.Serie, v.Folio, Total = v.TotalCentavos + v.RedondeoCentavos }).ToDictionaryAsync(v => v.Id, ct);
        var movimientoIds = envios.Where(e => e.MovimientoId is not null).Select(e => e.MovimientoId!.Value).ToList();
        var movimientos = await db.MovimientosCaja.AsNoTracking().Where(m => movimientoIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Tipo, m.MontoCentavos }).ToDictionaryAsync(m => m.Id, ct);
        var deEleventaIds = envios.Where(e => e.DevolucionDeEleventaId is not null).Select(e => e.DevolucionDeEleventaId!.Value).ToList();
        var deEleventa = await db.Set<DevolucionDeEleventa>().AsNoTracking().Where(d => deEleventaIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Folio }).ToDictionaryAsync(d => d.Id, ct);

        var filas = new List<string[]>
        {
            new[]
            {
                "Envío", "Tipo", "Qué", "Venta", "Total", "Estado", "Creado", "Intentos", "Último intento", "Enviado", "Segundos hasta enviarse",
                "Folio de eleventa", "Ticket de eleventa", "Número en eleventa", "Folio repetido", "Por revisar porque", "Por revisar desde", "Último error",
            },
        };
        foreach (var e in envios)
        {
            var venta = e.VentaId is { } v && ventas.TryGetValue(v, out var x) ? x : null;
            var folio = venta?.Folio is { } f ? $"{venta.Serie}-{f:000000}" : "";
            var que = e.Tipo switch
            {
                TipoEnvioEleventa.Venta => $"Venta {folio}",
                TipoEnvioEleventa.Cancelacion => $"Cancelación de {folio}",
                TipoEnvioEleventa.Devolucion => $"Devolución de {folio}",
                TipoEnvioEleventa.MovimientoDeCaja when e.MovimientoId is { } m && movimientos.TryGetValue(m, out var mov) =>
                    $"{(mov.Tipo == Nucleo.Turnos.TipoMovimientoCaja.Salida ? "Salida" : "Entrada")} de efectivo de {Pesos(mov.MontoCentavos)}",
                TipoEnvioEleventa.MovimientoDeCaja => "Entrada o salida de efectivo",
                TipoEnvioEleventa.DevolucionDeEleventa when e.DevolucionDeEleventaId is { } d && deEleventa.TryGetValue(d, out var ida) =>
                    $"Devolución del ticket #{ida.Folio} de eleventa",
                TipoEnvioEleventa.DevolucionDeEleventa => "Devolución de un ticket de eleventa",
                _ => "Cierre del turno",
            };
            filas.Add(
            [
                e.Id.ToString(), e.Tipo.ToString(), que, folio, venta is null ? "" : Pesos(venta.Total), e.Estado.ToString(), Hora(e.CreadoUtc),
                e.Intentos.ToString(CultureInfo.InvariantCulture), Hora(e.UltimoIntentoUtc), Hora(e.EnviadaUtc),
                e.EnviadaUtc is { } enviada ? (enviada - e.CreadoUtc).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) : "",
                Numero(e.FolioEleventa), Numero(e.TicketEleventa), Numero(e.IdEleventa), e.FolioRepetido ? "sí" : "", e.PorRevisarPorque?.ToString() ?? "",
                Hora(e.PorRevisarUtc), e.Error ?? "",
            ]);
        }
        return filas;
    }

    /// <summary>La bitácora de esos días: con encabezados, de lo más viejo a lo más nuevo.</summary>
    private static async Task<List<string[]>> BitacoraAsync(PosDbContext db, DateTime desdeUtc, DateTime hastaUtc, CancellationToken ct)
    {
        var renglones = await db.Bitacora.AsNoTracking().Where(b => b.FechaUtc >= desdeUtc && b.FechaUtc < hastaUtc).OrderBy(b => b.FechaUtc).ToListAsync(ct);
        var cajas = await db.Cajas.AsNoTracking().Select(c => new { c.Id, c.Nombre }).ToDictionaryAsync(c => c.Id, c => c.Nombre, ct);
        var usuarios = await db.Usuarios.AsNoTracking().Select(u => new { u.Id, u.Nombre }).ToDictionaryAsync(u => u.Id, u => u.Nombre, ct);
        var ventaIds = renglones.Where(b => b.VentaId is not null).Select(b => b.VentaId!.Value).Distinct().ToList();
        var ventas = await db.Ventas.AsNoTracking().Where(v => ventaIds.Contains(v.Id)).Select(v => new { v.Id, v.Serie, v.Folio })
            .ToDictionaryAsync(v => v.Id, v => v.Folio is { } f ? $"{v.Serie}-{f:000000}" : "", ct);
        var filas = new List<string[]>
        {
            new[] { "Fecha", "Caja", "Usuario", "Acción", "Resultado", "Detalle", "Monto", "Venta", "Autorizó", "Sin conexión" },
        };
        foreach (var b in renglones)
            filas.Add(
            [
                Hora(b.FechaUtc), b.CajaId is { } c ? cajas.GetValueOrDefault(c, "") : "", b.UsuarioId is { } u ? usuarios.GetValueOrDefault(u, "") : "",
                Permisos.Nombre(b.Accion), b.Resultado.ToString(), b.Detalle, b.MontoCentavos is { } monto ? Pesos(monto) : "",
                b.VentaId is { } v ? ventas.GetValueOrDefault(v, "") : "", b.AutorizoId is { } a ? usuarios.GetValueOrDefault(a, "") : "", b.EnContingencia ? "sí" : "",
            ]);
        return filas;
    }

    private static string Hora(DateTime? utc) =>
        utc is { } u ? HoraMexico.ALocal(u).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "";

    private static string Numero(int? numero) => numero?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Pesos(long centavos) => (centavos / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// En UTF-8 con su marca (así Excel lee los acentos), separado por comas como lo abre Excel en México, y entre comillas lo que
    /// trae comas, comillas o saltos de línea.
    /// </summary>
    public static async Task EscribirCsvAsync(string ruta, IEnumerable<string[]> filas, CancellationToken ct = default)
    {
        var texto = new StringBuilder();
        foreach (var fila in filas)
            texto.Append(string.Join(',', fila.Select(Campo))).Append("\r\n");
        await File.WriteAllTextAsync(ruta, texto.ToString(), new UTF8Encoding(true), ct);
    }

    public static string Campo(string valor) =>
        valor.AsSpan().IndexOfAny(",\"\r\n") >= 0 || valor.Length != valor.Trim().Length ? $"\"{valor.Replace("\"", "\"\"")}\"" : valor;

    // ── La copia de la base, sin secretos ──

    /// <summary>
    /// La copia de la base que se lleva el .zip, sin secretos (revisión del PR #145): sin la huella del PIN de nadie (un PIN de 4
    /// cifras sale de ella en minutos), ni la de la llave de cada caja, ni los códigos para vincular Telegram. Es un archivo nuevo,
    /// hecho de la copia (que no se toca; nunca de la base en uso), y compactado: lo quitado no se queda en las páginas libres del
    /// archivo. Se arma como <c>.tmp</c> y solo toma su nombre después de buscar en todo el archivo cada huella que traía: con el
    /// nombre bueno nunca hay una copia con secretos. Si algo no se pudo, no queda.
    /// </summary>
    public static async Task CopiaSinSecretosAsync(string copia, string destino, CancellationToken ct = default)
    {
        if (!File.Exists(copia))
            throw new IOException($"No está la copia de la base {copia}.");
        if (EsLaBaseEnUso(copia) || EsLaBaseEnUso(destino))
            throw new InvalidOperationException("la base que usa el servicio no se toca, solo una copia");
        if (string.Equals(Path.GetFullPath(copia), Path.GetFullPath(destino), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("va en otro archivo: la copia de la que sale no se toca");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destino))!);
        File.Delete(destino);
        var temporal = destino + ".tmp";
        BorrarConSusArchivos(temporal);
        File.Copy(copia, temporal);
        try
        {
            List<string> huellas;
            await using (var conexion = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporal, Pooling = false }.ToString()))
            {
                await conexion.OpenAsync(ct);
                // Todo en el archivo (sin WAL, que dejaría lo de antes a un lado) y lo que se borra, en ceros.
                await EjecutarAsync(conexion, "PRAGMA journal_mode=DELETE;", ct);
                await EjecutarAsync(conexion, "PRAGMA secure_delete=ON;", ct);
                await using (var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(conexion).Options))
                {
                    huellas = await db.Usuarios.AsNoTracking().Where(u => u.PinHash != "").Select(u => u.PinHash).ToListAsync(ct);
                    huellas.AddRange(await db.Cajas.AsNoTracking().Where(c => c.LlaveHuella != null).Select(c => c.LlaveHuella!).ToListAsync(ct));
                    await db.Usuarios.ExecuteUpdateAsync(u => u.SetProperty(x => x.PinHash, ""), ct);
                    await db.Cajas.ExecuteUpdateAsync(c => c.SetProperty(x => x.LlaveHuella, (string?)null), ct);
                    await db.CodigosVinculacion.ExecuteDeleteAsync(ct);
                }
                await EjecutarAsync(conexion, "VACUUM;", ct);
            }
            if (new[] { "-wal", "-shm", "-journal" }.Any(fin => File.Exists(temporal + fin)))
                throw new InvalidOperationException("quedó un archivo de SQLite junto a la copia");
            if (HuellaEnElArchivo(temporal, huellas) is { } cual)
                throw new InvalidOperationException($"todavía trae la huella {cual}");
            File.Move(temporal, destino);
        }
        catch
        {
            try
            {
                BorrarConSusArchivos(temporal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Se queda como .tmp, que el script no mete al .zip.
            }
            throw;
        }
    }

    private static async Task EjecutarAsync(SqliteConnection conexion, string sql, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Si alguna de las huellas sigue en el archivo, en cualquier lado (también en una página libre): «del PIN de un usuario» o «de
    /// la llave de una caja». Se lee por partes: la base puede medir cientos de MB y la caja 2 tiene poca memoria.
    /// </summary>
    private static string? HuellaEnElArchivo(string ruta, IReadOnlyList<string> huellas)
    {
        var buscadas = huellas.Where(h => h.Length >= 16).Distinct(StringComparer.Ordinal).Select(Encoding.UTF8.GetBytes).ToList();
        if (buscadas.Count == 0)
            return null;
        var traslape = buscadas.Max(b => b.Length) - 1;
        var bufer = new byte[4 * 1024 * 1024 + traslape];
        using var flujo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.Read);
        var guardados = 0;
        int leidos;
        while ((leidos = flujo.Read(bufer, guardados, bufer.Length - guardados)) > 0)
        {
            var hay = guardados + leidos;
            foreach (var buscada in buscadas)
            {
                if (bufer.AsSpan(0, hay).IndexOf(buscada) >= 0)
                    return buscada.AsSpan().StartsWith("pbkdf2"u8) ? "del PIN de un usuario" : "de la llave de una caja";
            }
            guardados = Math.Min(traslape, hay);
            Array.Copy(bufer, hay - guardados, bufer, 0, guardados);
        }
        return null;
    }

    private static void BorrarConSusArchivos(string ruta)
    {
        foreach (var fin in new[] { "", "-wal", "-shm", "-journal" })
            File.Delete(ruta + fin);
    }

    // ── La configuración de la PC, sin secretos ──

    public const string Quitado = "(quitado)";

    /// <summary>
    /// El JSON de la configuración (con comentarios, como servicio.json) sin secretos: lo que por su nombre es un secreto
    /// (<see cref="LoQueEsSecreto.PorSuNombre"/>: una clave, un código, una contraseña, un token, un PIN o una llave) se cambia por
    /// «(quitado)», y la contraseña dentro de una cadena de conexión («Password=», «UserPassword=») se tacha. Los comentarios no se
    /// copian (un ajuste comentado también puede traer una clave).
    /// </summary>
    public static string SinSecretos(string json)
    {
        var nodo = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        nodo = Limpiar(nodo);
        return (nodo?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null")
               + Environment.NewLine;
    }

    private static JsonNode? Limpiar(JsonNode? nodo)
    {
        switch (nodo)
        {
            case JsonObject objeto:
                foreach (var nombre in objeto.Select(p => p.Key).ToList())
                    objeto[nombre] = LoQueEsSecreto.PorSuNombre(nombre) && objeto[nombre] is not null ? JsonValue.Create(Quitado) : Limpiar(objeto[nombre]?.DeepClone());
                return objeto;
            case JsonArray arreglo:
                for (var i = 0; i < arreglo.Count; i++)
                    arreglo[i] = Limpiar(arreglo[i]?.DeepClone());
                return arreglo;
            case JsonValue valor when valor.TryGetValue<string>(out var texto):
                var limpio = LoQueEsSecreto.TacharEnUnaCadena(texto, Quitado);
                return limpio == texto ? valor : JsonValue.Create(limpio);
            default:
                return nodo;
        }
    }
}
