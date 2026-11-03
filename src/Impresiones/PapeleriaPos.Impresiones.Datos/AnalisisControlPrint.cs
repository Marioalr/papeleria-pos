using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones.Datos;

/// <summary>
/// Lo que dicen los datos de Control Print (2026-11-03, para el sábado): un informe corto para leerlo con el dueño, de una recopilación
/// (<c>control-print.db</c> de <c>recopilar-control-print</c>), con las preguntas abiertas de docs/impresion.md («Lo que solo dicen los
/// datos»): cuánto se imprime por PC, cola y mes; las copias del panel; las cotizaciones guardadas, usadas y cobradas; los precios puestos a
/// mano; las no vendidas; las hojas que faltaron; lo medido contra lo estimado; y el precio de Control Print contra el que daría el cálculo
/// del módulo para los mismos trabajos. <b>Solo lee</b> la base (de solo lectura) y no saca datos de los clientes: ni documentos, ni notas,
/// ni a quién se le puso un precio especial; los trabajos se nombran por su número y su día. Lo usa <c>analizar-control-print</c>.
/// </summary>
public static class AnalisisControlPrint
{
    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    private static readonly JsonSerializerOptions Json = new(CostSettings.Json) { PropertyNameCaseInsensitive = true };

    /// <summary>Lo guardado en el desglose de cada trabajo de Control Print: con qué ajustes salió y la tinta de cada página.</summary>
    private sealed record Desglose(JobSettings? Settings, List<PageCoverage>? Pages);

    /// <summary>El informe en Markdown.</summary>
    /// <param name="archivo">El <c>control-print.db</c> de una recopilación.</param>
    /// <param name="costosDelPos">Los costos de la sucursal en el POS, para comparar también con ellos (vacío: solo con los de Control Print).</param>
    /// <param name="deDondeSonLosDelPos">Para decir de dónde son («la base pos.db, sucursal M»).</param>
    public static string Analizar(string archivo, CostSettings? costosDelPos = null, string? deDondeSonLosDelPos = null)
    {
        if (!File.Exists(archivo))
            throw new FileNotFoundException($"No existe {archivo}.", archivo);
        using var conexion = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = archivo, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        conexion.Open();
        var texto = new StringBuilder();
        Encabezado(conexion, texto);
        PorMesPcYCola(conexion, texto);
        CopiasDelPanel(conexion, texto);
        Cotizaciones(conexion, texto);
        PreciosPuestos(conexion, texto);
        NoVendidas(conexion, texto);
        HojasYMedicion(conexion, texto);
        Precios(conexion, texto, costosDelPos, deDondeSonLosDelPos);
        return texto.ToString();
    }

    // ── Las partes ───────────────────────────────────────────────────────────────────────────────────────────────

    private static void Encabezado(SqliteConnection c, StringBuilder t)
    {
        var datos = Filas(c, "SELECT clave, valor FROM recopilacion", l => (l.GetString(0), l.IsDBNull(1) ? "" : l.GetString(1)))
            .ToDictionary(x => x.Item1, x => x.Item2);
        var (desde, hasta, total) = Fila(c, "SELECT MIN(dia), MAX(dia), COUNT(*) FROM trabajos",
            l => (l.IsDBNull(0) ? "—" : l.GetString(0), l.IsDBNull(1) ? "—" : l.GetString(1), l.GetInt64(2)));
        t.AppendLine("# Lo que dicen los datos de Control Print");
        t.AppendLine();
        var corte = DateTime.TryParse(datos.GetValueOrDefault("hasta_utc"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var hastaUtc)
            ? $"{PapeleriaPos.Nucleo.HoraMexico.ALocal(hastaUtc):yyyy-MM-dd HH:mm} (hora de la tienda)"
            : Valor(datos, "hasta_utc");
        t.AppendLine($"Recopilación de {Valor(datos, "nombre_servidor")} (Control Print {Valor(datos, "version_control_print")}): {Numero(total)} trabajos del "
            + $"{desde} al {hasta}, leídos hasta el {corte}. Estado: {Valor(datos, "estado")}.");
        if (datos.GetValueOrDefault("diferencia") is { Length: > 0 } diferencia)
            t.AppendLine($"**No cuadra con lo que cuenta Control Print:** {diferencia}. Hay que volver a correr `recopilar-control-print`.");
        if (datos.GetValueOrDefault("avisos") is { Length: > 2 } avisos && JsonSerializer.Deserialize<List<string>>(avisos) is { Count: > 0 } lista)
            foreach (var aviso in lista)
                t.AppendLine($"- Aviso de la recopilación: {aviso}");
        t.AppendLine();
        t.AppendLine("Solo se leyó la copia de sus datos. Aquí no van documentos, notas ni nombres de clientes: los trabajos van por su número.");
        t.AppendLine();
    }

    private static void PorMesPcYCola(SqliteConnection c, StringBuilder t)
    {
        t.AppendLine("## Trabajos por mes, PC y cola");
        t.AppendLine();
        t.AppendLine("Las impresiones desde las PC (sin las copias del panel). «Cobrado» y «por cobrar», con el precio que se cobra (el puesto o el calculado).");
        t.AppendLine();
        var filas = Filas(c, """
            SELECT substr(dia, 1, 7), pc_origen, impresora, COUNT(*), SUM(caras),
                   SUM(CASE WHEN cobro = 'Cobrado' THEN precio_a_cobrar ELSE 0 END),
                   SUM(CASE WHEN cobro = 'PorCobrar' AND cobrable = 1 THEN precio_a_cobrar ELSE 0 END),
                   SUM(CASE WHEN cobro = 'NoVendido' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN estado IN ('Cancelado', 'SinAnalizar') THEN 1 ELSE 0 END)
            FROM trabajos WHERE tipo = 'Impresion'
            GROUP BY 1, 2, 3 ORDER BY 1, 2, 3
            """, l => new[]
        {
            l.GetString(0), l.GetString(1), l.GetString(2), Numero(l.GetInt64(3)), Numero(l.GetInt64(4)), Pesos(l.GetDouble(5)), Pesos(l.GetDouble(6)),
            Numero(l.GetInt64(7)), Numero(l.GetInt64(8)),
        });
        Tabla(t, ["Mes", "PC", "Cola", "Trabajos", "Caras", "Cobrado", "Por cobrar", "No vendidas", "Canceladas o sin analizar"], filas);
        var colas = Filas(c, "SELECT DISTINCT impresora FROM trabajos WHERE tipo = 'Impresion' ORDER BY 1", l => l.GetString(0));
        t.AppendLine(colas.Count switch
        {
            0 => "No hay impresiones desde las PC.",
            1 => "Todo salió de una sola cola.",
            _ => $"Se imprimió en {colas.Count} colas: {string.Join(", ", colas.Select(x => $"«{x}»"))}. En el módulo, cada una es de una impresora (`ColasImpresion`).",
        });
        t.AppendLine();
    }

    private static void CopiasDelPanel(SqliteConnection c, StringBuilder t)
    {
        t.AppendLine("## Copias del panel");
        t.AppendLine();
        var filas = Filas(c, """
            SELECT substr(dia, 1, 7), COUNT(*), SUM(caras), SUM(caras_color), SUM(precio_a_cobrar)
            FROM trabajos WHERE tipo = 'CopiaEnElPanel' GROUP BY 1 ORDER BY 1
            """, l => new[] { l.GetString(0), Numero(l.GetInt64(1)), Numero(l.GetInt64(2)), Numero(l.GetInt64(3)), Pesos(l.GetDouble(4)) });
        if (filas.Count == 0)
        {
            t.AppendLine("No se registraron copias del panel en Control Print.");
            t.AppendLine();
            return;
        }
        Tabla(t, ["Mes", "Registros", "Caras", "A color", "Cobrado"], filas, izquierda: 1);
        var (copias, impresas) = Fila(c, """
            SELECT COALESCE(SUM(CASE WHEN tipo = 'CopiaEnElPanel' THEN caras END), 0), COALESCE(SUM(CASE WHEN tipo = 'Impresion' THEN caras END), 0)
            FROM trabajos
            """, l => (l.GetInt64(0), l.GetInt64(1)));
        t.AppendLine($"Las copias del panel son {Porcentaje(copias, copias + impresas)} de las caras registradas. Lo que la impresora contó y nadie "
            + "registró (el celular, copias sin cobrar) se ve en el contador del módulo, no aquí.");
        t.AppendLine();
    }

    private static void Cotizaciones(SqliteConnection c, StringBuilder t)
    {
        t.AppendLine("## Cotizaciones");
        t.AppendLine();
        var (guardadas, usadas, especiales) = Fila(c,
            "SELECT COUNT(*), SUM(CASE WHEN trabajo_id IS NOT NULL THEN 1 ELSE 0 END), SUM(especial) FROM cotizaciones",
            l => (l.GetInt64(0), l.IsDBNull(1) ? 0 : l.GetInt64(1), l.IsDBNull(2) ? 0 : l.GetInt64(2)));
        var cobradas = Fila(c, """
            SELECT COUNT(*) FROM cotizaciones q JOIN trabajos j ON j.id = q.trabajo_id
            WHERE j.cobro = 'Cobrado' AND j.tipo_precio = 'Cotizado'
            """, l => l.GetInt64(0));
        var conPrecioCotizado = Fila(c, "SELECT COUNT(*) FROM trabajos WHERE tipo_precio = 'Cotizado'", l => l.GetInt64(0));
        var hasta = Fila(c, "SELECT MAX(detectado_utc) FROM trabajos", l => l.IsDBNull(0) ? null : l.GetString(0));
        var vencidas = hasta is null ? 0 : Fila(c, "SELECT COUNT(*) FROM cotizaciones WHERE trabajo_id IS NULL AND vale_hasta_utc < $hasta",
            l => l.GetInt64(0), ("$hasta", hasta));
        if (guardadas == 0)
        {
            t.AppendLine("No hay cotizaciones guardadas: en Control Print casi no se usó «Guardar cotización» (o no se guardó ninguna).");
            t.AppendLine();
            return;
        }
        t.AppendLine($"- Guardadas: {Numero(guardadas)} ({Numero(especiales)} con precio especial).");
        t.AppendLine($"- Usadas (ligadas a un trabajo impreso): {Numero(usadas)}, {Porcentaje(usadas, guardadas)}.");
        t.AppendLine($"- Cobradas con el precio cotizado: {Numero(cobradas)}; trabajos con precio «cotizado»: {Numero(conPrecioCotizado)}.");
        t.AppendLine($"- Vencidas sin usarse: {Numero(vencidas)}.");
        t.AppendLine();
    }

    private static void PreciosPuestos(SqliteConnection c, StringBuilder t)
    {
        t.AppendLine("## Precios puestos (no el calculado)");
        t.AppendLine();
        var filas = Filas(c, """
            SELECT tipo_precio, COUNT(*), SUM(precio_puesto - precio), SUM(CASE WHEN precio_puesto < precio THEN 1 ELSE 0 END),
                   COUNT(DISTINCT cliente_del_precio)
            FROM trabajos WHERE precio_puesto IS NOT NULL AND tipo = 'Impresion'
            GROUP BY tipo_precio ORDER BY 2 DESC
            """, l => new[]
        {
            TipoDePrecio(l.GetString(0)), Numero(l.GetInt64(1)), Pesos(l.GetDouble(2)), Numero(l.GetInt64(3)), Numero(l.GetInt64(4)),
        });
        var total = Fila(c, "SELECT COUNT(*) FROM trabajos WHERE tipo = 'Impresion' AND cobrable = 1", l => l.GetInt64(0));
        if (filas.Count == 0)
        {
            t.AppendLine("Ningún trabajo con precio puesto: todo se cobró con el calculado.");
            t.AppendLine();
            return;
        }
        Tabla(t, ["Cómo", "Trabajos", "Contra el calculado", "Más baratos", "Clientes distintos"], filas, izquierda: 1);
        var puestos = filas.Sum(f => long.Parse(f[1], NumberStyles.AllowThousands, CultureInfo.InvariantCulture));
        t.AppendLine($"Con precio puesto: {Porcentaje(puestos, total)} de los trabajos con precio. «Contra el calculado» es lo que se cobró de más (o de "
            + "menos, negativo) contra el precio que calculó Control Print. De los clientes solo se cuenta cuántos son.");
        var quien = Filas(c, """
            SELECT COALESCE(precio_puesto_por, '(sin nombre)'), COUNT(*) FROM trabajos
            WHERE precio_puesto IS NOT NULL AND tipo_precio IN ('AMano', 'Especial') GROUP BY 1 ORDER BY 2 DESC
            """, l => $"{l.GetString(0)} ({Numero(l.GetInt64(1))})");
        if (quien.Count > 0)
            t.AppendLine($"A mano o especial, por quién: {string.Join(", ", quien)}.");
        t.AppendLine();
    }

    private static void NoVendidas(SqliteConnection c, StringBuilder t)
    {
        t.AppendLine("## No vendidas");
        t.AppendLine();
        var filas = Filas(c, """
            SELECT COALESCE(motivo_no_vendida, 'Desconocido'), COUNT(*), SUM(costo_total), SUM(caras)
            FROM trabajos WHERE cobro = 'NoVendido' GROUP BY 1 ORDER BY 2 DESC
            """, l => new[] { Motivo(l.GetString(0)), Numero(l.GetInt64(1)), Pesos(l.GetDouble(2)), Numero(l.GetInt64(3)) });
        if (filas.Count == 0)
        {
            t.AppendLine("Ninguna marcada como no vendida.");
            t.AppendLine();
            return;
        }
        Tabla(t, ["Motivo", "Trabajos", "Costo perdido", "Caras"], filas, izquierda: 1);
        var total = Fila(c, "SELECT COUNT(*) FROM trabajos WHERE cobrable = 1 OR cobro = 'NoVendido'", l => l.GetInt64(0));
        var noVendidas = filas.Sum(f => long.Parse(f[1], NumberStyles.AllowThousands, CultureInfo.InvariantCulture));
        t.AppendLine($"Son {Porcentaje(noVendidas, total)} de los trabajos con precio. Las notas no se copian aquí.");
        t.AppendLine();
    }

    private static void HojasYMedicion(SqliteConnection c, StringBuilder t)
    {
        t.AppendLine("## Hojas que faltaron y lo que se midió");
        t.AppendLine();
        var (faltaron, soloLoQueSalio, noVendidas, pendientes) = Fila(c, """
            SELECT COUNT(*), SUM(CASE WHEN tipo_precio = 'SoloLoQueSalio' THEN 1 ELSE 0 END), SUM(CASE WHEN cobro = 'NoVendido' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN cobro = 'PorCobrar' THEN 1 ELSE 0 END)
            FROM trabajos WHERE faltaron_caras = 1
            """, l => (l.GetInt64(0), l.IsDBNull(1) ? 0 : l.GetInt64(1), l.IsDBNull(2) ? 0 : l.GetInt64(2), l.IsDBNull(3) ? 0 : l.GetInt64(3)));
        t.AppendLine(faltaron == 0
            ? "- Ningún trabajo con hojas que faltaron (la impresora contó todas)."
            : $"- Con hojas que faltaron (atasco o cancelación): {Numero(faltaron)}; se cobró solo lo que salió en {Numero(soloLoQueSalio)}, "
              + $"no vendidas {Numero(noVendidas)} y siguen por cobrar {Numero(pendientes)}.");
        var medicion = Filas(c, """
            SELECT pc_origen, impresora, SUM(CASE WHEN nivel_analisis = 'Medido' THEN 1 ELSE 0 END), SUM(CASE WHEN nivel_analisis = 'Estimado' THEN 1 ELSE 0 END)
            FROM trabajos WHERE tipo = 'Impresion' GROUP BY 1, 2 ORDER BY 1, 2
            """, l => new[] { l.GetString(0), l.GetString(1), Numero(l.GetInt64(2)), Numero(l.GetInt64(3)) });
        t.AppendLine("- Lo medido página por página contra lo estimado por páginas (una cola sin EMF o sin «Conservar documentos impresos» solo estima):");
        t.AppendLine();
        Tabla(t, ["PC", "Cola", "Medidos", "Estimados"], medicion, izquierda: 2);
    }

    private static void Precios(SqliteConnection c, StringBuilder t, CostSettings? costosDelPos, string? deDondeSonLosDelPos)
    {
        t.AppendLine("## El precio de Control Print contra el del módulo");
        t.AppendLine();
        var deControlPrint = Filas(c, "SELECT json FROM configuracion WHERE clave = 'costos'", l => l.IsDBNull(0) ? null : l.GetString(0))
            .FirstOrDefault() is { } json
            ? JsonSerializer.Deserialize<CostSettings>(json, Json)
            : null;
        if (deControlPrint is null && costosDelPos is null)
        {
            t.AppendLine("La recopilación no trae los costos de Control Print: no se puede volver a calcular.");
            t.AppendLine();
            return;
        }
        var trabajos = Filas(c, """
            SELECT id, dia, caras, precio, precio_a_cobrar, costo_total, desglose_json, nivel_analisis
            FROM trabajos WHERE tipo = 'Impresion' AND cobrable = 1 AND desglose_json IS NOT NULL
            """, l => (Id: l.GetInt64(0), Dia: l.GetString(1), Caras: l.GetInt32(2), Precio: (decimal)l.GetDouble(3), Cobrado: (decimal)l.GetDouble(4),
                Costo: (decimal)l.GetDouble(5), Desglose: l.GetString(6), Medido: !l.IsDBNull(7) && l.GetString(7) == "Medido"));
        var analisis = new List<(long Id, string Dia, int Caras, decimal Precio, decimal Cobrado, decimal Costo, JobAnalysis Analisis)>();
        var sinDesglose = 0;
        foreach (var x in trabajos)
        {
            Desglose? d;
            try
            {
                d = JsonSerializer.Deserialize<Desglose>(x.Desglose, Json);
            }
            catch (JsonException)
            {
                d = null;
            }
            if (d?.Settings is null || d.Pages is not { Count: > 0 })
            {
                sinDesglose++;
                continue;
            }
            analisis.Add((x.Id, x.Dia, x.Caras, x.Precio, x.Cobrado, x.Costo,
                new JobAnalysis(d.Settings, d.Pages, x.Medido ? AnalysisLevel.Measured : AnalysisLevel.Estimated, null, null, TimeSpan.Zero)));
        }
        t.AppendLine($"Se volvieron a calcular {Numero(analisis.Count)} trabajos con su desglose (la tinta de cada página y sus ajustes)"
            + (sinDesglose > 0 ? $"; {Numero(sinDesglose)} no lo traían completo." : "."));
        t.AppendLine();
        if (analisis.Count == 0)
            return;
        if (deControlPrint is not null)
            Comparar(t, "Con los costos que tiene Control Print hoy", deControlPrint, analisis,
                "Si no coinciden, casi siempre es porque los costos cambiaron después de imprimir (Control Print guarda el precio de ese momento): "
                + "lo que importa es que el cálculo del módulo es el mismo.");
        if (costosDelPos is not null)
            Comparar(t, $"Con los costos del POS{(deDondeSonLosDelPos is null ? "" : $" ({deDondeSonLosDelPos})")}", costosDelPos, analisis,
                "Es lo que el módulo habría cobrado por lo mismo con los costos de la Configuración (las impresoras que usan los cartuchos de la sucursal).");
    }

    private static void Comparar(StringBuilder t, string titulo, CostSettings costos,
        List<(long Id, string Dia, int Caras, decimal Precio, decimal Cobrado, decimal Costo, JobAnalysis Analisis)> trabajos, string nota)
    {
        var calculados = trabajos.Select(x => (x.Id, x.Dia, x.Caras, x.Precio, x.Cobrado, x.Costo, Modulo: CostCalculator.Calculate(x.Analisis, costos)))
            .ToList();
        var iguales = calculados.Count(x => x.Modulo.Price == x.Precio);
        var sumaControlPrint = calculados.Sum(x => x.Precio);
        var sumaCobrado = calculados.Sum(x => x.Cobrado);
        var sumaModulo = calculados.Sum(x => x.Modulo.Price);
        t.AppendLine($"### {titulo}");
        t.AppendLine();
        t.AppendLine($"- El mismo precio que calculó Control Print: {Numero(iguales)} de {Numero(calculados.Count)} ({Porcentaje(iguales, calculados.Count)}).");
        t.AppendLine($"- En total: Control Print calculó {Pesos(sumaControlPrint)} y cobra {Pesos(sumaCobrado)} (con los precios puestos); el módulo, "
            + $"{Pesos(sumaModulo)} ({Diferencia(sumaModulo - sumaControlPrint)} contra lo calculado).");
        t.AppendLine($"- {nota}");
        var distintos = calculados.Where(x => x.Modulo.Price != x.Precio).OrderByDescending(x => Math.Abs(x.Modulo.Price - x.Precio)).Take(10).ToList();
        if (distintos.Count > 0)
        {
            t.AppendLine();
            t.AppendLine("Los que más se alejan:");
            t.AppendLine();
            Tabla(t, ["Trabajo", "Día", "Caras", "Control Print", "Módulo", "Costo en Control Print", "Costo en el módulo"], izquierda: 2, filas:
                distintos.Select(x => new[]
                {
                    $"#{x.Id}", x.Dia, Numero(x.Caras), Pesos(x.Precio), Pesos(x.Modulo.Price), Pesos(x.Costo), Pesos(x.Modulo.Total),
                }).ToList());
        }
        else
        {
            t.AppendLine();
        }
    }

    // ── Ayudas ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static string TipoDePrecio(string tipo) => tipo switch
    {
        "AMano" => "A mano",
        "Especial" => "Especial (con nombre)",
        "SoloLoQueSalio" => "Solo lo que salió",
        "Copias" => "Copias (lo cobrado)",
        "Cotizado" => "Cotizado",
        "Calculado" => "Calculado",
        _ => tipo,
    };

    private static string Motivo(string motivo) => motivo switch
    {
        "NoEraLoQueQueria" => "No era lo que quería",
        "SalioMal" => "Salió mal",
        "Atasco" => "Atasco",
        "Otro" => "Otro",
        _ => motivo,
    };

    private static string Valor(Dictionary<string, string> datos, string clave) => datos.GetValueOrDefault(clave) is { Length: > 0 } v ? v : "—";

    private static string Numero(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Pesos(double pesos) => Pesos((decimal)pesos);

    private static string Pesos(decimal pesos) => pesos.ToString("C2", Mexico);

    private static string Diferencia(decimal pesos) => pesos == 0 ? "igual" : pesos > 0 ? $"{Pesos(pesos)} más" : $"{Pesos(-pesos)} menos";

    private static string Porcentaje(long parte, long total) => total <= 0 ? "—" : $"{(double)parte / total * 100:0.#} %";

    /// <param name="izquierda">Cuántas columnas son texto (a la izquierda); las demás son números (a la derecha).</param>
    private static void Tabla(StringBuilder t, IReadOnlyList<string> encabezados, IReadOnlyList<string[]> filas, int izquierda = 3)
    {
        if (filas.Count == 0)
        {
            t.AppendLine("(nada)");
            t.AppendLine();
            return;
        }
        t.AppendLine($"| {string.Join(" | ", encabezados)} |");
        t.AppendLine($"|{string.Join("|", encabezados.Select((_, i) => i < izquierda ? "---" : "---:"))}|");
        foreach (var fila in filas)
            t.AppendLine($"| {string.Join(" | ", fila.Select(Celda))} |");
        t.AppendLine();
    }

    /// <summary>Un texto en una celda de Markdown: sin barras ni saltos de línea.</summary>
    private static string Celda(string texto) => texto.Replace("|", "/").Replace('\n', ' ').Replace('\r', ' ');

    private static List<T> Filas<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> leer, params (string Nombre, object Valor)[] parametros)
    {
        using var comando = c.CreateCommand();
        comando.CommandText = sql;
        foreach (var (nombre, valor) in parametros)
            comando.Parameters.AddWithValue(nombre, valor);
        using var lector = comando.ExecuteReader();
        var filas = new List<T>();
        while (lector.Read())
            filas.Add(leer(lector));
        return filas;
    }

    private static T Fila<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> leer, params (string Nombre, object Valor)[] parametros) =>
        Filas(c, sql, leer, parametros).Single();
}
