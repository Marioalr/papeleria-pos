using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FirebirdSql.Data.FirebirdClient;

namespace PapeleriaPos.Eleventa;

/// <summary>Una tabla en una foto: sus renglones por llave, con cada valor como texto.</summary>
/// <param name="Completa">Si están todos los renglones; si no, solo los últimos (por su llave numérica).</param>
public sealed record TablaEnFoto(string Nombre, IReadOnlyList<string> Columnas, IReadOnlyList<string> Llave, long Filas, bool Completa,
    IReadOnlyDictionary<string, string[]> Renglones);

/// <summary>
/// Toda la base de eleventa en un momento, solo leyendo (docs/plan.md, el piloto, paso 2): los contadores y los renglones de
/// cada tabla (las grandes, solo los últimos). Comparando dos fotos se ve qué escribe eleventa en una venta, una cancelación
/// o un corte, sin escribir nada en su base.
/// </summary>
public sealed record FotoEleventa(DateTime TomadaUtc, IReadOnlyDictionary<string, long> Contadores, IReadOnlyDictionary<string, TablaEnFoto> Tablas)
{
    /// <summary>Hasta cuántos renglones se guarda una tabla completa; de las más grandes, solo los últimos.</summary>
    public const int LimiteCompleta = 5000;

    public const int UltimosRenglones = 300;

    /// <summary>
    /// Lo que la foto nunca lee (2026-10-25): la clave de cada usuario de eleventa y sus permisos. Las comparaciones se guardan
    /// en archivos de texto (observar-eleventa), y una clave nueva o cambiada habría salido ahí. Además, en todas las tablas, las
    /// columnas que parecen secretas (<see cref="PareceSecreta"/>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> NuncaSeLeen = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["USUARIOS"] = ["CLAVE", "PERMISOS"],
    };

    /// <summary>
    /// Lo que en el nombre de una columna dice que puede ser secreta (revisión del PR #144): la foto no la lee, ni su valor ni su
    /// huella. En la base de la tienda (2026-10-24) son <c>USUARIOS.CLAVE</c>, la clave y la llave privada del certificado de
    /// facturación (<c>FACTURACION_CERTIFICADOS.CLAVE_LLAVE_PRIVADA</c> y <c>CONTENIDO_LLAVEPRIVADA</c>) y, de paso, unas claves del SAT
    /// y <c>SINCRONIZACION_NUBE.LLAVE_ID</c>, que no son secretas: mejor de más que de menos.
    /// </summary>
    public static readonly string[] Secretas = ["CLAVE", "CONTRASE", "PASS", "PWD", "LLAVE", "SECRET", "TOKEN", "PFX", "KEY"];

    public static bool PareceSecreta(string columna) => Secretas.Any(s => columna.Contains(s, StringComparison.OrdinalIgnoreCase));

    public static async Task<FotoEleventa> TomarAsync(ServidorEleventa servidor, CancellationToken ct = default)
    {
        await using var conexion = new FbConnection(servidor.Cadena());
        await conexion.OpenAsync(ct);
        return await TomarAsync(conexion, ct);
    }

    public static async Task<FotoEleventa> TomarAsync(FbConnection c, CancellationToken ct = default)
    {
        await using var tx = await c.BeginTransactionAsync(new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Read | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
        }, ct);
        var tomada = DateTime.UtcNow;

        // Por la red (el Wi-Fi de la tienda) cada consulta es un viaje de ida y vuelta: lo que se puede, va junto.
        var generadores = await ColumnaAsync(c, tx, "SELECT TRIM(RDB$GENERATOR_NAME) FROM RDB$GENERATORS WHERE COALESCE(RDB$SYSTEM_FLAG, 0) = 0", ct);
        var valores = await UnRenglonAsync(c, tx, generadores.Select(g => $"GEN_ID(\"{g}\", 0)"), ct);
        var contadores = generadores.Zip(valores).ToDictionary(x => x.First, x => x.Second, StringComparer.Ordinal);

        var nombres = await ColumnaAsync(c, tx,
            "SELECT TRIM(RDB$RELATION_NAME) FROM RDB$RELATIONS WHERE COALESCE(RDB$SYSTEM_FLAG, 0) = 0 AND RDB$VIEW_BLR IS NULL ORDER BY 1", ct);
        var filas = await UnRenglonAsync(c, tx, nombres.Select(t => $"(SELECT COUNT(*) FROM \"{t}\")"), ct);
        var llaves = await LlavesAsync(c, tx, ct);
        // Las columnas de todas las tablas, en una consulta: de las que tienen algo que no se lee (lo de NuncaSeLeen o lo que parece
        // secreto), sus columnas sin eso, con su nombre en lugar de «*». Si eso era de su llave, la llave queda sin ella.
        var columnasDe = await ColumnasAsync(c, tx, ct);
        var tablas = new Dictionary<string, TablaEnFoto>(StringComparer.Ordinal);
        for (var i = 0; i < nombres.Count; i++)
        {
            var columnas = columnasDe.GetValueOrDefault(nombres[i]) ?? [];
            var nunca = NuncaSeLeen.GetValueOrDefault(nombres[i]) ?? [];
            var omitidas = columnas.Where(columna => PareceSecreta(columna) || nunca.Contains(columna, StringComparer.Ordinal)).ToHashSet(StringComparer.Ordinal);
            var seleccion = omitidas.Count == 0 ? "*" : string.Join(", ", columnas.Where(x => !omitidas.Contains(x)).Select(x => $"\"{x}\""));
            var llave = (llaves.GetValueOrDefault(nombres[i]) ?? []).Where(x => !omitidas.Contains(x)).ToList();
            tablas[nombres[i]] = await TablaAsync(c, tx, nombres[i], llave, filas[i], seleccion.Length > 0 ? seleccion : null, ct);
        }

        await tx.CommitAsync(ct);
        return new FotoEleventa(tomada, contadores, tablas);
    }

    /// <summary>Varias expresiones en una sola consulta (un solo viaje): <c>SELECT a, b, c FROM RDB$DATABASE</c>.</summary>
    private static async Task<long[]> UnRenglonAsync(FbConnection c, FbTransaction tx, IEnumerable<string> expresiones, CancellationToken ct)
    {
        var lista = expresiones.ToList();
        if (lista.Count == 0)
            return [];
        await using var comando = new FbCommand($"SELECT {string.Join(", ", lista)} FROM RDB$DATABASE", c, tx);
        await using var lector = await comando.ExecuteReaderAsync(ct);
        await lector.ReadAsync(ct);
        return Enumerable.Range(0, lista.Count).Select(i => Convert.ToInt64(lector.GetValue(i), CultureInfo.InvariantCulture)).ToArray();
    }

    /// <summary>Las columnas de cada tabla, en su orden. Todas en una consulta.</summary>
    private static async Task<Dictionary<string, List<string>>> ColumnasAsync(FbConnection c, FbTransaction tx, CancellationToken ct)
    {
        await using var comando = new FbCommand("""
            SELECT TRIM(f.RDB$RELATION_NAME), TRIM(f.RDB$FIELD_NAME)
            FROM RDB$RELATION_FIELDS f
            JOIN RDB$RELATIONS r ON r.RDB$RELATION_NAME = f.RDB$RELATION_NAME
            WHERE COALESCE(r.RDB$SYSTEM_FLAG, 0) = 0 AND r.RDB$VIEW_BLR IS NULL
            ORDER BY f.RDB$RELATION_NAME, f.RDB$FIELD_POSITION
            """, c, tx) { FetchSize = 2000 };
        await using var lector = await comando.ExecuteReaderAsync(ct);
        var columnas = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        while (await lector.ReadAsync(ct))
        {
            var tabla = lector.GetString(0);
            if (!columnas.TryGetValue(tabla, out var deLaTabla))
                columnas[tabla] = deLaTabla = [];
            deLaTabla.Add(lector.GetString(1));
        }
        return columnas;
    }

    /// <summary>La llave de cada tabla: la primaria o, si no tiene, su primera restricción única. Todas en una consulta.</summary>
    private static async Task<Dictionary<string, List<string>>> LlavesAsync(FbConnection c, FbTransaction tx, CancellationToken ct)
    {
        await using var comando = new FbCommand("""
            SELECT TRIM(r.RDB$RELATION_NAME), TRIM(r.RDB$CONSTRAINT_NAME), TRIM(s.RDB$FIELD_NAME)
            FROM RDB$RELATION_CONSTRAINTS r
            JOIN RDB$INDEX_SEGMENTS s ON s.RDB$INDEX_NAME = r.RDB$INDEX_NAME
            WHERE r.RDB$CONSTRAINT_TYPE IN ('PRIMARY KEY', 'UNIQUE')
            ORDER BY 1, r.RDB$CONSTRAINT_TYPE, 2, s.RDB$FIELD_POSITION
            """, c, tx);
        await using var lector = await comando.ExecuteReaderAsync(ct);
        var llaves = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var restriccion = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await lector.ReadAsync(ct))
        {
            var (tabla, nombre, columna) = (lector.GetString(0), lector.GetString(1), lector.GetString(2));
            // «PRIMARY KEY» va antes que «UNIQUE»: la primera restricción de cada tabla es la que queda.
            if (restriccion.TryAdd(tabla, nombre))
                llaves[tabla] = [];
            if (restriccion[tabla] == nombre)
                llaves[tabla].Add(columna);
        }
        return llaves;
    }

    /// <param name="seleccion">
    /// Lo que se pide de cada renglón: «*», o sus columnas sin lo que no se lee (<see cref="NuncaSeLeen"/>, <see cref="PareceSecreta"/>).
    /// Vacío si no queda ninguna: la tabla solo se cuenta.
    /// </param>
    private static async Task<TablaEnFoto> TablaAsync(FbConnection c, FbTransaction tx, string tabla, List<string> llave, long filas, string? seleccion,
        CancellationToken ct)
    {
        var completa = filas <= LimiteCompleta || llave.Count != 1;
        // Sin llave, o con una de varias columnas, una tabla grande solo se cuenta.
        var sql = seleccion is null ? null
            : completa
            ? filas <= LimiteCompleta ? $"SELECT {seleccion} FROM \"{tabla}\"" : null
            : $"SELECT FIRST {UltimosRenglones} {seleccion} FROM \"{tabla}\" ORDER BY \"{llave[0]}\" DESC";
        var columnas = new List<string>();
        var renglones = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (sql is not null)
        {
            // De a muchos renglones por viaje (el cliente pide 200 si no se le dice).
            await using var comando = new FbCommand(sql, c, tx) { FetchSize = 2000 };
            await using var lector = await comando.ExecuteReaderAsync(ct);
            for (var i = 0; i < lector.FieldCount; i++)
                columnas.Add(lector.GetName(i));
            var posiciones = llave.Count > 0 ? llave.Select(lector.GetOrdinal).ToArray() : [];
            var numero = 0;
            while (await lector.ReadAsync(ct))
            {
                var valores = new string[lector.FieldCount];
                for (var i = 0; i < valores.Length; i++)
                    valores[i] = Texto(lector, i);
                // Sin llave, el renglón entero es su llave (si hay dos iguales, se numeran).
                var clave = posiciones.Length > 0 ? string.Join("|", posiciones.Select(p => valores[p])) : string.Join("|", valores);
                if (!renglones.TryAdd(clave, valores))
                    renglones[$"{clave}#{++numero}"] = valores;
            }
        }
        return new TablaEnFoto(tabla, columnas, llave, filas, completa && sql is not null, renglones);
    }


    private static async Task<List<string>> ColumnaAsync(FbConnection c, FbTransaction tx, string sql, CancellationToken ct)
    {
        await using var comando = new FbCommand(sql, c, tx);
        await using var lector = await comando.ExecuteReaderAsync(ct);
        var valores = new List<string>();
        while (await lector.ReadAsync(ct))
            valores.Add(lector.GetString(0));
        return valores;
    }

    /// <summary>Cada valor como texto estable: lo mismo da lo mismo entre una foto y otra.</summary>
    private static string Texto(DbDataReader r, int i)
    {
        if (r.IsDBNull(i))
            return "∅";
        return r.GetValue(i) switch
        {
            byte[] bytes => $"blob {bytes.Length} B {Convert.ToHexString(SHA256.HashData(bytes))[..8]}",
            string s => s.TrimEnd(),
            DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var otro => otro.ToString() ?? "",
        };
    }
}

/// <summary>Lo que cambió entre dos fotos: contadores, renglones nuevos, cambiados y quitados, tabla por tabla.</summary>
public static class ComparacionEleventa
{
    public static string Comparar(FotoEleventa antes, FotoEleventa despues)
    {
        var texto = new StringBuilder();
        var contadores = despues.Contadores
            .Where(c => antes.Contadores.GetValueOrDefault(c.Key) != c.Value)
            .Select(c => $"{c.Key} {antes.Contadores.GetValueOrDefault(c.Key)} → {c.Value} (+{c.Value - antes.Contadores.GetValueOrDefault(c.Key)})")
            .ToList();
        if (contadores.Count > 0)
            texto.AppendLine("Contadores: " + string.Join("; ", contadores));

        foreach (var (nombre, nueva) in despues.Tablas)
        {
            if (!antes.Tablas.TryGetValue(nombre, out var vieja))
            {
                texto.AppendLine($"{nombre}: tabla nueva ({nueva.Filas} renglones)");
                continue;
            }
            var cambios = Tabla(vieja, nueva);
            if (cambios.Count == 0 && vieja.Filas == nueva.Filas)
                continue;
            var resumen = vieja.Filas == nueva.Filas ? "" : $" ({vieja.Filas} → {nueva.Filas} renglones)";
            texto.AppendLine($"{nombre}{resumen}");
            foreach (var cambio in cambios)
                texto.AppendLine("  " + cambio);
        }
        foreach (var nombre in antes.Tablas.Keys.Where(n => !despues.Tablas.ContainsKey(n)))
            texto.AppendLine($"{nombre}: ya no está");
        return texto.Length == 0 ? "Sin cambios." : texto.ToString();
    }

    private static List<string> Tabla(TablaEnFoto antes, TablaEnFoto despues)
    {
        // De una tabla grande solo se tienen los últimos renglones (por su llave): uno que ya no sale porque quedó debajo de
        // la ventana no se borró, y uno nuevo debajo de la ventana anterior no se puede saber.
        var desdeAntes = antes.Completa ? (long?)null : Menor(antes);
        var desdeDespues = despues.Completa ? (long?)null : Menor(despues);
        static bool Dentro(string clave, long? desde) => desde is null || Numero(clave) is { } n && n >= desde;

        var lineas = new List<string>();
        foreach (var (clave, valores) in despues.Renglones)
        {
            if (antes.Renglones.TryGetValue(clave, out var anteriores))
            {
                var distintos = despues.Columnas.Select((columna, i) => (columna, i))
                    .Where(x => x.i < anteriores.Length && anteriores[x.i] != valores[x.i])
                    .Select(x => $"{x.columna} {anteriores[x.i]} → {valores[x.i]}")
                    .ToList();
                if (distintos.Count > 0)
                    lineas.Add($"~ {Llave(despues, clave)}: {string.Join(", ", distintos)}");
            }
            else if (Dentro(clave, desdeAntes))
                lineas.Add($"+ {Llave(despues, clave)}: {Valores(despues, valores)}");
        }
        foreach (var (clave, valores) in antes.Renglones)
        {
            if (!despues.Renglones.ContainsKey(clave) && Dentro(clave, desdeDespues))
                lineas.Add($"- {Llave(antes, clave)}: {Valores(antes, valores)}");
        }
        return lineas;
    }

    private static long? Menor(TablaEnFoto tabla) => tabla.Renglones.Keys.Select(Numero).Where(n => n is not null).Min();

    private static long? Numero(string clave) => long.TryParse(clave, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string Llave(TablaEnFoto tabla, string clave) =>
        tabla.Llave.Count > 0 ? $"{string.Join("|", tabla.Llave)}={clave}" : "renglón";

    /// <summary>Sin la llave (ya va al principio) ni lo vacío.</summary>
    private static string Valores(TablaEnFoto tabla, string[] valores) =>
        string.Join(", ", tabla.Columnas.Select((columna, i) => (columna, valor: valores[i]))
            .Where(x => x.valor != "∅" && !tabla.Llave.Contains(x.columna)).Select(x => $"{x.columna}={x.valor}"));
}
