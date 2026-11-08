using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Impresiones.ControlPrint;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Datos;

/// <summary>
/// La base SQLite de una recopilación de Control Print (<see cref="NombreArchivo"/>): sus tablas, guardar en una
/// transacción por página y lo que se cuenta para el README. Las fechas van en UTC (ISO 8601, se ordenan como texto) y, para
/// leerlas, también el día y la hora de México; el dinero, en pesos como lo guarda Control Print; los enum, con su nombre
/// en español. Nunca guarda el código de conexión, contraseñas ni sesiones: de los usuarios solo lo que los identifica, de
/// la configuración se quita lo que parezca un secreto, y ningún texto que lleve el código se guarda con él.
/// </summary>
internal sealed partial class ArchivoRecopilacion : IDisposable
{
    public const string NombreArchivo = "control-print.db";

    /// <summary>Sube cuando cambian las tablas: una recopilación sin terminar de otra versión no se sigue.</summary>
    public const string VersionEsquema = "2";

    private readonly SqliteConnection _conexion;
    private readonly Func<string, bool> _llevaElCodigo;

    private ArchivoRecopilacion(SqliteConnection conexion, Func<string, bool> llevaElCodigo)
    {
        _conexion = conexion;
        _llevaElCodigo = llevaElCodigo;
    }

    /// <param name="llevaElCodigo">Si un texto lleva el código de conexión (<see cref="ClienteControlPrint.LlevaElCodigo"/>): ese texto no se guarda.</param>
    public static ArchivoRecopilacion Abrir(string carpeta, Func<string, bool>? llevaElCodigo = null)
    {
        // Sin pool: al cerrarla, el archivo queda libre. Diario normal (no WAL): un solo archivo al terminar, y si se corta a
        // media página, SQLite deshace esa página al volver a abrirla.
        var conexion = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(carpeta, NombreArchivo),
            Pooling = false,
        }.ToString());
        conexion.Open();
        var archivo = new ArchivoRecopilacion(conexion, llevaElCodigo ?? (_ => false));
        archivo.Ejecutar(Esquema);
        return archivo;
    }

    /// <summary>La recopilación de esa carpeta, si tiene una (sin crearla).</summary>
    public static IReadOnlyDictionary<string, string>? LeerRecopilacion(string carpeta)
    {
        var ruta = Path.Combine(carpeta, NombreArchivo);
        if (!File.Exists(ruta))
            return null;
        try
        {
            using var conexion = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = ruta,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            conexion.Open();
            using var comando = conexion.CreateCommand();
            comando.CommandText = "SELECT clave, valor FROM recopilacion";
            using var lector = comando.ExecuteReader();
            var datos = new Dictionary<string, string>();
            while (lector.Read())
                datos[lector.GetString(0)] = lector.IsDBNull(1) ? "" : lector.GetString(1);
            return datos;
        }
        catch (SqliteException)
        {
            return new Dictionary<string, string>();
        }
    }

    // ── Las tablas ───────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Columna<T>(string Nombre, string Tipo, Func<T, object?> Valor);

    private static readonly Columna<TrabajoControlPrint>[] ColumnasTrabajos =
    [
        new("id", "INTEGER PRIMARY KEY", t => t.Id),
        new("tipo", "TEXT NOT NULL", t => t.Tipo.ToString()),
        new("pc_origen", "TEXT NOT NULL", t => t.PcOrigen),
        new("impresora", "TEXT NOT NULL", t => t.Impresora),
        new("numero_en_la_cola", "INTEGER NOT NULL", t => t.NumeroEnLaCola),
        new("documento", "TEXT NOT NULL", t => t.Documento),
        new("etiqueta", "TEXT", t => t.Etiqueta),
        new("nombre_que_se_ve", "TEXT", t => t.NombreQueSeVe),
        new("usuario_windows", "TEXT NOT NULL", t => t.UsuarioWindows),
        new("equipo", "TEXT NOT NULL", t => t.Equipo),
        new("tipo_de_datos", "TEXT NOT NULL", t => t.TipoDeDatos),
        new("enviado_utc", "TEXT NOT NULL", t => Fecha(t.EnviadoUtc)),
        new("detectado_utc", "TEXT NOT NULL", t => Fecha(t.DetectadoUtc)),
        new("dia", "TEXT NOT NULL", t => Dia(t.DetectadoUtc)),
        new("hora_local", "TEXT NOT NULL", t => HoraLocal(t.DetectadoUtc)),
        new("analizado_utc", "TEXT", t => Fecha(t.AnalizadoUtc)),
        new("terminado_utc", "TEXT", t => Fecha(t.TerminadoUtc)),
        new("estado", "TEXT NOT NULL", t => t.Estado.ToString()),
        new("nivel_analisis", "TEXT", t => t.NivelAnalisis?.ToString()),
        new("nota_analisis", "TEXT", t => t.NotaAnalisis),
        new("paginas", "INTEGER NOT NULL", t => t.Paginas),
        new("copias", "INTEGER NOT NULL", t => t.Copias),
        new("caras", "INTEGER NOT NULL", t => t.Caras),
        new("caras_color", "INTEGER NOT NULL", t => t.CarasColor),
        new("caras_contadas", "INTEGER", t => t.CarasContadas),
        new("faltaron_caras", "INTEGER NOT NULL", t => t.FaltaronCaras),
        new("hojas", "INTEGER NOT NULL", t => t.Hojas),
        new("color", "INTEGER NOT NULL", t => t.Color),
        new("calidad", "TEXT NOT NULL", t => t.Calidad),
        new("tipo_papel", "TEXT NOT NULL", t => t.TipoPapel),
        new("nombre_papel", "TEXT NOT NULL", t => t.NombrePapel),
        new("tamano_papel", "TEXT NOT NULL", t => t.TamanoPapel),
        new("doble_cara", "INTEGER NOT NULL", t => t.DobleCara),
        new("paginas_por_hoja", "INTEGER NOT NULL", t => t.PaginasPorHoja),
        new("tinta_c", "REAL NOT NULL", t => t.TintaC),
        new("tinta_m", "REAL NOT NULL", t => t.TintaM),
        new("tinta_y", "REAL NOT NULL", t => t.TintaY),
        new("tinta_k", "REAL NOT NULL", t => t.TintaK),
        new("costo_tinta", "REAL NOT NULL", t => t.CostoTinta),
        new("costo_papel", "REAL NOT NULL", t => t.CostoPapel),
        new("costo_mantenimiento", "REAL NOT NULL", t => t.CostoMantenimiento),
        new("costo_luz", "REAL NOT NULL", t => t.CostoLuz),
        new("costo_extras", "REAL NOT NULL", t => t.CostoExtras),
        new("costo_depreciacion", "REAL NOT NULL", t => t.CostoDepreciacion),
        new("costo_total", "REAL NOT NULL", t => t.CostoTotal),
        new("margen_porcentaje", "REAL NOT NULL", t => t.MargenPorcentaje),
        new("margen_papel_porcentaje", "REAL", t => t.MargenPapelPorcentaje),
        new("precio", "REAL NOT NULL", t => t.Precio),
        new("precio_puesto", "REAL", t => t.PrecioPuesto),
        new("tipo_precio", "TEXT NOT NULL", t => t.TipoPrecio.ToString()),
        new("precio_puesto_utc", "TEXT", t => Fecha(t.PrecioPuestoUtc)),
        new("precio_puesto_por", "TEXT", t => t.PrecioPuestoPor),
        new("cliente_del_precio", "TEXT", t => t.ClienteDelPrecio),
        new("precio_a_cobrar", "REAL NOT NULL", t => t.PrecioACobrar),
        new("cobrable", "INTEGER NOT NULL", t => t.Cobrable),
        new("cobro", "TEXT NOT NULL", t => t.Cobro.ToString()),
        new("cobro_por", "TEXT", t => t.CobroPor),
        new("cobro_utc", "TEXT", t => Fecha(t.CobroUtc)),
        new("motivo_no_vendida", "TEXT", t => t.MotivoNoVendida?.ToString()),
        new("nota_no_vendida", "TEXT", t => t.NotaNoVendida),
        new("notas", "TEXT", t => t.Notas),
        new("atendio_id", "INTEGER", t => t.AtendioId),
        new("atendio_nombre", "TEXT", t => t.AtendioNombre),
        new("reasignado_por", "TEXT", t => t.ReasignadoPor),
        new("reasignado_utc", "TEXT", t => Fecha(t.ReasignadoUtc)),
        new("desglose_json", "TEXT", t => t.DesgloseJson),
        new("otros_json", "TEXT", t => Otros(t.Otros)),
    ];

    private static readonly Columna<CotizacionControlPrint>[] ColumnasCotizaciones =
    [
        new("id", "INTEGER PRIMARY KEY", c => c.Id),
        new("creada_utc", "TEXT NOT NULL", c => Fecha(c.CreadaUtc)),
        new("dia", "TEXT NOT NULL", c => Dia(c.CreadaUtc)),
        new("creada_por", "TEXT", c => c.CreadaPor),
        new("usuario_id", "INTEGER", c => c.UsuarioId),
        new("pc_origen", "TEXT NOT NULL", c => c.PcOrigen),
        new("archivo", "TEXT NOT NULL", c => c.Archivo),
        new("paginas_documento", "INTEGER NOT NULL", c => c.PaginasDocumento),
        new("rango", "TEXT", c => c.Rango),
        new("paginas", "INTEGER NOT NULL", c => c.Paginas),
        new("copias", "INTEGER NOT NULL", c => c.Copias),
        new("caras", "INTEGER NOT NULL", c => c.Caras),
        new("hojas", "INTEGER NOT NULL", c => c.Hojas),
        new("caras_color", "INTEGER NOT NULL", c => c.CarasColor),
        new("opciones_json", "TEXT", c => c.Opciones.ValueKind == JsonValueKind.Undefined ? null : c.Opciones.GetRawText()),
        new("costo_total", "REAL NOT NULL", c => c.CostoTotal),
        new("precio", "REAL NOT NULL", c => c.Precio),
        new("cotizado", "REAL NOT NULL", c => c.Cotizado),
        new("especial", "INTEGER NOT NULL", c => c.Especial),
        new("cliente", "TEXT", c => c.Cliente),
        new("resumen", "TEXT NOT NULL", c => c.Resumen),
        new("vale_hasta_utc", "TEXT NOT NULL", c => Fecha(c.ValeHastaUtc)),
        new("trabajo_id", "INTEGER", c => c.TrabajoId),
        new("otros_json", "TEXT", c => Otros(c.Otros)),
    ];

    private static readonly Columna<LecturaImpresoraControlPrint>[] ColumnasLecturas =
    [
        new("tomada_utc", "TEXT NOT NULL", l => Fecha(l.TomadaUtc)),
        new("hora_local", "TEXT NOT NULL", l => HoraLocal(l.TomadaUtc)),
        new("tinta_k", "INTEGER", l => l.TintaK),
        new("tinta_c", "INTEGER", l => l.TintaC),
        new("tinta_m", "INTEGER", l => l.TintaM),
        new("tinta_y", "INTEGER", l => l.TintaY),
        new("caja_mantenimiento", "INTEGER", l => l.CajaMantenimiento),
        new("contador_total", "INTEGER", l => l.ContadorTotal),
        new("contador_color", "INTEGER", l => l.ContadorColor),
        new("contador_bn", "INTEGER", l => l.ContadorBN),
        new("serie", "TEXT", l => l.Serie),
        new("errores_json", "TEXT", l => l.Errores is { Count: > 0 } errores ? JsonSerializer.Serialize(errores) : null),
        new("otros_json", "TEXT", l => Otros(l.Otros)),
    ];

    private static readonly string Esquema = $"""
        CREATE TABLE IF NOT EXISTS recopilacion (clave TEXT PRIMARY KEY, valor TEXT);
        CREATE TABLE IF NOT EXISTS progreso (ventana TEXT PRIMARY KEY, desde_utc TEXT, hasta_utc TEXT, trabajos INTEGER NOT NULL, terminada_utc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS trabajos ({Definicion(ColumnasTrabajos)}, miniatura TEXT);
        CREATE INDEX IF NOT EXISTS trabajos_detectado ON trabajos (detectado_utc);
        CREATE INDEX IF NOT EXISTS trabajos_dia ON trabajos (dia);
        CREATE VIEW IF NOT EXISTS copias AS
            SELECT id, dia, hora_local, pc_origen, documento AS descripcion, caras, caras_color, color, doble_cara, tamano_papel,
                   precio_a_cobrar AS cobrado, costo_total, cobro, cobro_por, atendio_nombre, notas
            FROM trabajos WHERE tipo = '{TipoTrabajoControlPrint.CopiaEnElPanel}';
        CREATE TABLE IF NOT EXISTS resumen_por_dia (dia TEXT PRIMARY KEY, trabajos INTEGER NOT NULL, caras INTEGER NOT NULL, costo REAL NOT NULL,
            vendido REAL NOT NULL, cobrado REAL NOT NULL, por_cobrar REAL NOT NULL, no_vendidas INTEGER NOT NULL, costo_no_vendidas REAL NOT NULL,
            calculado INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS caras_por_dia (dia TEXT PRIMARY KEY, caras_impresas INTEGER NOT NULL, caras_impresas_color INTEGER NOT NULL,
            caras_copias INTEGER NOT NULL, caras_copias_color INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS cotizaciones ({Definicion(ColumnasCotizaciones)});
        CREATE TABLE IF NOT EXISTS lecturas_impresora ({Definicion(ColumnasLecturas)});
        CREATE INDEX IF NOT EXISTS lecturas_tomada ON lecturas_impresora (tomada_utc);
        CREATE TABLE IF NOT EXISTS usuarios (id INTEGER PRIMARY KEY, nombre TEXT NOT NULL, rol TEXT NOT NULL, activo INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS configuracion (clave TEXT PRIMARY KEY, json TEXT NOT NULL);
        """;

    private static string Definicion<T>(Columna<T>[] columnas) => string.Join(", ", columnas.Select(c => $"{c.Nombre} {c.Tipo}"));

    // ── Guardar ──────────────────────────────────────────────────────────────────────────────────────────────────

    public void GuardarValor(string clave, string? valor)
    {
        using var comando = _conexion.CreateCommand();
        comando.CommandText = "INSERT INTO recopilacion (clave, valor) VALUES ($c, $v) ON CONFLICT(clave) DO UPDATE SET valor = excluded.valor";
        comando.Parameters.AddWithValue("$c", clave);
        comando.Parameters.AddWithValue("$v", (object?)valor ?? DBNull.Value);
        comando.ExecuteNonQuery();
    }

    public string? Valor(string clave) =>
        Escalar("SELECT valor FROM recopilacion WHERE clave = $c", ("$c", clave)) as string;

    /// <summary>Una página de trabajos, toda o nada. Si ya estaban, se ponen al día (sin perder su miniatura).</summary>
    public void GuardarTrabajos(IEnumerable<TrabajoControlPrint> trabajos) =>
        Guardar("trabajos", ColumnasTrabajos, trabajos, actualizar: true);

    public void GuardarCotizaciones(IEnumerable<CotizacionControlPrint> cotizaciones) =>
        Guardar("cotizaciones", ColumnasCotizaciones, cotizaciones, actualizar: true);

    /// <summary>Cambia las lecturas desde <paramref name="desdeUtc"/> (todas, sin fecha) por las que se leyeron.</summary>
    public void CambiarLecturas(DateTime? desdeUtc, IEnumerable<LecturaImpresoraControlPrint> lecturas)
    {
        using var transaccion = _conexion.BeginTransaction();
        using (var borrar = _conexion.CreateCommand())
        {
            borrar.Transaction = transaccion;
            borrar.CommandText = "DELETE FROM lecturas_impresora WHERE $desde IS NULL OR tomada_utc >= $desde";
            borrar.Parameters.AddWithValue("$desde", (object?)Fecha(desdeUtc) ?? DBNull.Value);
            borrar.ExecuteNonQuery();
        }
        Insertar(transaccion, "lecturas_impresora", ColumnasLecturas, lecturas, actualizar: false);
        transaccion.Commit();
    }

    public void CambiarUsuarios(IEnumerable<UsuarioControlPrint> usuarios)
    {
        using var transaccion = _conexion.BeginTransaction();
        using var comando = _conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = "DELETE FROM usuarios";
        comando.ExecuteNonQuery();
        comando.CommandText = "INSERT INTO usuarios (id, nombre, rol, activo) VALUES ($id, $nombre, $rol, $activo)";
        foreach (var usuario in usuarios)
        {
            comando.Parameters.Clear();
            comando.Parameters.AddWithValue("$id", usuario.Id);
            comando.Parameters.AddWithValue("$nombre", usuario.Nombre);
            comando.Parameters.AddWithValue("$rol", usuario.Rol.ToString());
            comando.Parameters.AddWithValue("$activo", usuario.Activo);
            comando.ExecuteNonQuery();
        }
        transaccion.Commit();
    }

    /// <summary>
    /// Una configuración (o la calibración) en JSON, sin lo que parezca un secreto: por el nombre del campo o porque lleva el código
    /// de conexión. Devuelve los campos que se quitaron.
    /// </summary>
    public IReadOnlyList<string> GuardarConfiguracion(string clave, string json)
    {
        var (limpio, quitados) = SinSecretos(json, _llevaElCodigo);
        using var comando = _conexion.CreateCommand();
        comando.CommandText = "INSERT INTO configuracion (clave, json) VALUES ($c, $j) ON CONFLICT(clave) DO UPDATE SET json = excluded.json";
        comando.Parameters.AddWithValue("$c", clave);
        comando.Parameters.AddWithValue("$j", limpio);
        comando.ExecuteNonQuery();
        return quitados;
    }

    /// <param name="calculado">El resumen se calculó de los trabajos (<see cref="ResumenDeLosTrabajos"/>), no es el de Control Print.</param>
    public void GuardarDia(DateOnly dia, ResumenDiaControlPrint resumen, CarasRegistradasControlPrint caras, bool calculado = false)
    {
        using var transaccion = _conexion.BeginTransaction();
        using var comando = _conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = """
            INSERT OR REPLACE INTO resumen_por_dia (dia, trabajos, caras, costo, vendido, cobrado, por_cobrar, no_vendidas, costo_no_vendidas, calculado)
            VALUES ($dia, $trabajos, $caras, $costo, $vendido, $cobrado, $porCobrar, $noVendidas, $costoNoVendidas, $calculado)
            """;
        var texto = dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        comando.Parameters.AddWithValue("$dia", texto);
        comando.Parameters.AddWithValue("$trabajos", resumen.Trabajos);
        comando.Parameters.AddWithValue("$caras", resumen.Caras);
        comando.Parameters.AddWithValue("$costo", resumen.Costo);
        comando.Parameters.AddWithValue("$vendido", resumen.Precio);
        comando.Parameters.AddWithValue("$cobrado", resumen.Cobrado);
        comando.Parameters.AddWithValue("$porCobrar", resumen.PorCobrar);
        comando.Parameters.AddWithValue("$noVendidas", resumen.NoVendidas);
        comando.Parameters.AddWithValue("$costoNoVendidas", resumen.CostoNoVendidas);
        comando.Parameters.AddWithValue("$calculado", calculado ? 1 : 0);
        comando.ExecuteNonQuery();
        comando.Parameters.Clear();
        comando.CommandText = """
            INSERT OR REPLACE INTO caras_por_dia (dia, caras_impresas, caras_impresas_color, caras_copias, caras_copias_color)
            VALUES ($dia, $impresas, $impresasColor, $copias, $copiasColor)
            """;
        comando.Parameters.AddWithValue("$dia", texto);
        comando.Parameters.AddWithValue("$impresas", caras.CarasImpresas);
        comando.Parameters.AddWithValue("$impresasColor", caras.CarasImpresasColor);
        comando.Parameters.AddWithValue("$copias", caras.CarasCopias);
        comando.Parameters.AddWithValue("$copiasColor", caras.CarasCopiasColor);
        comando.ExecuteNonQuery();
        transaccion.Commit();
    }

    /// <summary>Dónde quedó la miniatura de cada trabajo («miniaturas/123.png»; vacío: no tiene).</summary>
    public void GuardarMiniaturas(IEnumerable<(long Id, string Ruta)> miniaturas)
    {
        using var transaccion = _conexion.BeginTransaction();
        using var comando = _conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = "UPDATE trabajos SET miniatura = $ruta WHERE id = $id";
        var id = comando.Parameters.Add("$id", SqliteType.Integer);
        var ruta = comando.Parameters.Add("$ruta", SqliteType.Text);
        foreach (var miniatura in miniaturas)
        {
            id.Value = miniatura.Id;
            ruta.Value = miniatura.Ruta;
            comando.ExecuteNonQuery();
        }
        transaccion.Commit();
    }

    public void TerminarVentana(string ventana, DateTime? desdeUtc, DateTime hastaUtc, int trabajos, DateTime ahoraUtc)
    {
        using var comando = _conexion.CreateCommand();
        comando.CommandText = """
            INSERT OR REPLACE INTO progreso (ventana, desde_utc, hasta_utc, trabajos, terminada_utc) VALUES ($v, $desde, $hasta, $n, $ahora)
            """;
        comando.Parameters.AddWithValue("$v", ventana);
        comando.Parameters.AddWithValue("$desde", (object?)Fecha(desdeUtc) ?? DBNull.Value);
        comando.Parameters.AddWithValue("$hasta", Fecha(hastaUtc));
        comando.Parameters.AddWithValue("$n", trabajos);
        comando.Parameters.AddWithValue("$ahora", Fecha(ahoraUtc));
        comando.ExecuteNonQuery();
    }

    /// <summary>Quita el resumen y las caras de los días de [desde, hasta) (sin desde: todos los de antes): se vuelven a pedir.</summary>
    public void OlvidarDias(DateTime? desdeUtc, DateTime hastaUtc)
    {
        using var transaccion = _conexion.BeginTransaction();
        foreach (var tabla in new[] { "resumen_por_dia", "caras_por_dia" })
        {
            using var comando = _conexion.CreateCommand();
            comando.Transaction = transaccion;
            comando.CommandText = $"DELETE FROM {tabla} WHERE ($desde IS NULL OR dia >= $desde) AND dia <= $hasta";
            comando.Parameters.AddWithValue("$desde", desdeUtc is { } desde ? Dia(desde) : (object)DBNull.Value);
            comando.Parameters.AddWithValue("$hasta", Dia(hastaUtc.AddTicks(-1)));
            comando.ExecuteNonQuery();
        }
        transaccion.Commit();
    }

    private void Guardar<T>(string tabla, Columna<T>[] columnas, IEnumerable<T> filas, bool actualizar)
    {
        using var transaccion = _conexion.BeginTransaction();
        Insertar(transaccion, tabla, columnas, filas, actualizar);
        transaccion.Commit();
    }

    private void Insertar<T>(SqliteTransaction transaccion, string tabla, Columna<T>[] columnas, IEnumerable<T> filas, bool actualizar)
    {
        using var comando = _conexion.CreateCommand();
        comando.Transaction = transaccion;
        var nombres = string.Join(", ", columnas.Select(c => c.Nombre));
        var valores = string.Join(", ", columnas.Select((_, i) => $"$p{i}"));
        comando.CommandText = $"INSERT INTO {tabla} ({nombres}) VALUES ({valores})" + (actualizar
            ? $" ON CONFLICT({columnas[0].Nombre}) DO UPDATE SET {string.Join(", ", columnas.Skip(1).Select(c => $"{c.Nombre} = excluded.{c.Nombre}"))}"
            : "");
        // Sin tipo fijo: cada valor se guarda con el suyo (número, texto o NULL).
        var parametros = columnas.Select((_, i) => comando.Parameters.Add(new SqliteParameter { ParameterName = $"$p{i}" })).ToArray();
        foreach (var fila in filas)
        {
            for (var i = 0; i < columnas.Length; i++)
                parametros[i].Value = columnas[i].Valor(fila) switch
                {
                    null => DBNull.Value,
                    bool b => b ? 1 : 0,
                    decimal d => (double)d,
                    // Nunca el código de conexión, aunque Control Print lo mande en un texto (una nota, un campo nuevo): de un JSON se
                    // quita lo que lo lleva; otro texto se queda vacío.
                    string texto when _llevaElCodigo(texto) => columnas[i].Nombre.EndsWith("_json", StringComparison.Ordinal)
                        ? SinSecretos(texto, _llevaElCodigo, porNombre: false).Limpio
                        : "",
                    var v => v,
                };
            comando.ExecuteNonQuery();
        }
    }

    // ── Leer y contar ────────────────────────────────────────────────────────────────────────────────────────────

    public HashSet<string> VentanasTerminadas() =>
        Lista("SELECT ventana FROM progreso", l => l.GetString(0)).ToHashSet();

    public int ContarTrabajos(DateTime? desdeUtc, DateTime hastaUtc) => Convert.ToInt32(Escalar(
        "SELECT COUNT(*) FROM trabajos WHERE ($desde IS NULL OR detectado_utc >= $desde) AND detectado_utc < $hasta",
        ("$desde", Fecha(desdeUtc)), ("$hasta", Fecha(hastaUtc))), CultureInfo.InvariantCulture);

    /// <summary>Los días con trabajos que todavía no tienen su resumen y sus caras.</summary>
    public IReadOnlyList<DateOnly> DiasSinResumen() => Lista("""
        SELECT DISTINCT dia FROM trabajos
        WHERE dia NOT IN (SELECT dia FROM resumen_por_dia) OR dia NOT IN (SELECT dia FROM caras_por_dia)
        ORDER BY dia
        """, l => DateOnly.ParseExact(l.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>
    /// El resumen de un día calculado de los trabajos guardados hasta <paramref name="hastaUtc"/>, como lo calcula Control Print
    /// (JobRepository.SummaryFor): los que tienen precio (analizados o impresos), sus caras contadas o si no las suyas, el costo,
    /// lo vendido sin las no vendidas, lo cobrado y el costo de las no vendidas aparte. Para el día que no había terminado a la
    /// hora de corte: Control Print solo da el de días enteros.
    /// </summary>
    public ResumenDiaControlPrint ResumenDeLosTrabajos(DateOnly dia, DateTime hastaUtc)
    {
        using var comando = _conexion.CreateCommand();
        comando.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(COALESCE(caras_contadas, caras)), 0), COALESCE(SUM(costo_total), 0),
                   COALESCE(SUM(CASE WHEN cobro <> $noVendido THEN precio_a_cobrar ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN cobro = $cobrado THEN precio_a_cobrar ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN cobro = $noVendido THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN cobro = $noVendido THEN costo_total ELSE 0 END), 0)
            FROM trabajos WHERE dia = $dia AND detectado_utc < $hasta AND estado IN ($analizado, $impreso)
            """;
        comando.Parameters.AddWithValue("$dia", dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        comando.Parameters.AddWithValue("$hasta", Fecha(hastaUtc));
        comando.Parameters.AddWithValue("$analizado", EstadoTrabajoControlPrint.Analizado.ToString());
        comando.Parameters.AddWithValue("$impreso", EstadoTrabajoControlPrint.Impreso.ToString());
        comando.Parameters.AddWithValue("$noVendido", CobroControlPrint.NoVendido.ToString());
        comando.Parameters.AddWithValue("$cobrado", CobroControlPrint.Cobrado.ToString());
        using var lector = comando.ExecuteReader();
        lector.Read();
        var vendido = (decimal)lector.GetDouble(3);
        var cobrado = (decimal)lector.GetDouble(4);
        return new ResumenDiaControlPrint(lector.GetInt32(0), lector.GetInt32(1), (decimal)lector.GetDouble(2), vendido, cobrado,
            lector.GetInt32(5), (decimal)lector.GetDouble(6), vendido - cobrado);
    }

    /// <summary>Los días cuyo resumen se calculó de los trabajos (<see cref="ResumenDeLosTrabajos"/>).</summary>
    public IReadOnlyList<string> DiasCalculados() =>
        Lista("SELECT dia FROM resumen_por_dia WHERE calculado = 1 ORDER BY dia", l => l.GetString(0));

    public IReadOnlyList<long> TrabajosSinMiniatura() =>
        Lista("SELECT id FROM trabajos WHERE miniatura IS NULL ORDER BY id", l => l.GetInt64(0));

    public long Contar(string consulta) => Convert.ToInt64(Escalar(consulta), CultureInfo.InvariantCulture);

    public IReadOnlyList<(string Nombre, long Cuantos)> ContarPor(string columna, string tabla = "trabajos") =>
        Lista($"SELECT COALESCE({columna}, ''), COUNT(*) FROM {tabla} GROUP BY 1 ORDER BY 2 DESC, 1", l => (l.GetString(0), l.GetInt64(1)));

    public (string? Primero, string? Ultimo) RangoDeDias(string tabla = "trabajos") =>
        Lista($"SELECT MIN(dia), MAX(dia) FROM {tabla}", l => (l.IsDBNull(0) ? null : l.GetString(0), l.IsDBNull(1) ? null : l.GetString(1)))[0];

    /// <summary>Los campos que mandó Control Print y esta versión del POS no conoce (en «otros_json»).</summary>
    public IReadOnlyList<string> CamposDesconocidos() => Lista("""
        SELECT DISTINCT 'trabajos.' || j.key FROM trabajos, json_each(trabajos.otros_json) AS j WHERE trabajos.otros_json IS NOT NULL
        UNION SELECT DISTINCT 'cotizaciones.' || j.key FROM cotizaciones, json_each(cotizaciones.otros_json) AS j WHERE cotizaciones.otros_json IS NOT NULL
        UNION SELECT DISTINCT 'lecturas_impresora.' || j.key FROM lecturas_impresora, json_each(lecturas_impresora.otros_json) AS j
            WHERE lecturas_impresora.otros_json IS NOT NULL
        ORDER BY 1
        """, l => l.GetString(0));

    private object? Escalar(string consulta, params (string Nombre, object? Valor)[] parametros)
    {
        using var comando = _conexion.CreateCommand();
        comando.CommandText = consulta;
        foreach (var (nombre, valor) in parametros)
            comando.Parameters.AddWithValue(nombre, valor ?? DBNull.Value);
        return comando.ExecuteScalar();
    }

    private List<T> Lista<T>(string consulta, Func<SqliteDataReader, T> leer)
    {
        using var comando = _conexion.CreateCommand();
        comando.CommandText = consulta;
        using var lector = comando.ExecuteReader();
        var lista = new List<T>();
        while (lector.Read())
            lista.Add(leer(lector));
        return lista;
    }

    private void Ejecutar(string sql)
    {
        using var comando = _conexion.CreateCommand();
        comando.CommandText = sql;
        comando.ExecuteNonQuery();
    }

    public void Dispose() => _conexion.Dispose();

    // ── Formatos ─────────────────────────────────────────────────────────────────────────────────────────────────

    internal static string? Fecha(DateTime? utc) => utc is { } f ? Fecha(f) : null;

    internal static string Fecha(DateTime utc) =>
        (utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToString("O", CultureInfo.InvariantCulture);

    internal static string Dia(DateTime utc) => HoraMexico.ALocal(utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string HoraLocal(DateTime utc) => HoraMexico.ALocal(utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string? Otros(Dictionary<string, JsonElement>? otros) =>
        otros is { Count: > 0 } ? SinSecretos(JsonSerializer.Serialize(otros)).Limpio : null;

    /// <summary>
    /// Quita de un JSON, a cualquier profundidad, los campos cuyo nombre parece de un secreto (<see cref="PareceSecreto"/>) y,
    /// por su valor, los textos que llevan el código de conexión (<paramref name="llevaElCodigo"/>), se llamen como se llamen.
    /// Control Print 1.12 no manda ninguno en su configuración; esto es por si una versión nueva lo hace. Lo que no es JSON no
    /// se guarda.
    /// </summary>
    /// <param name="porNombre">False: solo por el valor (en un JSON de un trabajo, cuyos nombres no se tocan).</param>
    internal static (string Limpio, IReadOnlyList<string> Quitados) SinSecretos(string json, Func<string, bool>? llevaElCodigo = null, bool porNombre = true)
    {
        JsonNode? nodo;
        try
        {
            nodo = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return ("null", ["(no era JSON: no se guardó)"]);
        }
        llevaElCodigo ??= _ => false;
        if (EsTextoConElCodigo(nodo, llevaElCodigo))
            return ("null", ["(todo: llevaba el código de conexión)"]);
        var quitados = new List<string>();
        Limpiar(nodo, "", quitados, llevaElCodigo, porNombre);
        return (nodo?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null", quitados.Distinct().ToList());
    }

    private static void Limpiar(JsonNode? nodo, string ruta, List<string> quitados, Func<string, bool> llevaElCodigo, bool porNombre)
    {
        switch (nodo)
        {
            case JsonObject objeto:
                foreach (var (nombre, valor) in objeto.ToList())
                {
                    if ((porNombre && PareceSecreto().IsMatch(nombre)) || EsTextoConElCodigo(valor, llevaElCodigo))
                    {
                        objeto.Remove(nombre);
                        quitados.Add(ruta + nombre);
                    }
                    else
                    {
                        Limpiar(valor, $"{ruta}{nombre}.", quitados, llevaElCodigo, porNombre);
                    }
                }
                break;
            case JsonArray arreglo:
                var conElCodigo = new List<int>();
                for (var i = 0; i < arreglo.Count; i++)
                {
                    if (EsTextoConElCodigo(arreglo[i], llevaElCodigo))
                        conElCodigo.Add(i);
                    else
                        Limpiar(arreglo[i], ruta, quitados, llevaElCodigo, porNombre);
                }
                for (var i = conElCodigo.Count - 1; i >= 0; i--)
                    arreglo.RemoveAt(conElCodigo[i]);
                if (conElCodigo.Count > 0)
                    quitados.Add(ruta.TrimEnd('.') + "[]");
                break;
        }
    }

    private static bool EsTextoConElCodigo(JsonNode? nodo, Func<string, bool> llevaElCodigo) =>
        nodo is JsonValue valor && valor.GetValueKind() == JsonValueKind.String && llevaElCodigo(valor.GetValue<string>());

    /// <summary>
    /// El nombre de un campo que parece de un secreto: una contraseña («pass», «contraseña», «clave»), un secreto, un token, un
    /// hash, una credencial, algo privado o protegido, el código de conexión, un certificado con su llave («pfx», «pkcs»), una
    /// cadena de conexión, un PIN y cualquier «…Key» compuesto («ApiKey», «TelegramBotKey»). No la «Key» sola: es la de cada
    /// papel en los costos (Media[].Key).
    /// </summary>
    [GeneratedRegex(@"pass|contrase|clave|secret|token|hash|credential|private|protected|connection[_-]?(code|key|string)|pfx|pkcs|^pin$|.keys?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PareceSecreto();
}
