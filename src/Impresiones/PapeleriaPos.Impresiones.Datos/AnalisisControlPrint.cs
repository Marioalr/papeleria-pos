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

    /// <summary>
    /// Si la recopilación no terminó (se cortó con Ctrl+C o por la red) o alguna parte no se pudo leer (las cotizaciones): lo que no trae no
    /// se concluye («casi no se usó»), se dice «no se sabe».
    /// </summary>
    private sealed record Completa(bool Terminada, string? Motivo, IReadOnlyList<string> Avisos)
    {
        public bool SinCotizaciones => !Terminada || Avisos.Any(a => a.Contains("cotizaciones", StringComparison.OrdinalIgnoreCase));

        public string NoSeSabe => Terminada
            ? "no se sabe: la recopilación avisó que no pudo leer esa parte."
            : $"no se sabe: la recopilación no terminó{(Motivo is { Length: > 0 } m ? $" ({m})" : "")} y no trae todo.";
    }

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
        // Recopilar se cortó a media escritura: su diario se deshace al abrirla para escribir, y aquí solo se lee. Se dice una vez y claro.
        if (File.Exists(archivo + "-journal"))
            return "# Lo que dicen los datos de Control Print\n\n**La recopilación se cortó a la mitad de una escritura** (junto a ella quedó "
                + $"«{Path.GetFileName(archivo)}-journal»): así no se puede leer. Vuelve a correr `recopilar-control-print` (sigue donde se quedó) y "
                + "después esta herramienta.\n";
        using var conexion = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = archivo, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        conexion.Open();
        var texto = new StringBuilder();
        var completa = new Completa(true, null, []);
        try
        {
            completa = LeerSiEstaCompleta(conexion);
        }
        catch (Exception ex) when (ex is SqliteException or JsonException)
        {
            completa = new Completa(false, ex.Message, []);
        }
        Action<SqliteConnection, StringBuilder>[] partes =
        [
            (c, t) => Encabezado(c, t, completa), PorMesPcYCola, (c, t) => CopiasDelPanel(c, t, completa), (c, t) => Cotizaciones(c, t, completa),
            (c, t) => CotizadoContraImpreso(c, t, completa), CalidadConQueSeImprime,
            (c, t) => PreciosPuestos(c, t, completa), (c, t) => NoVendidas(c, t, completa), (c, t) => HojasYMedicion(c, t, completa),
            (c, t) => Precios(c, t, costosDelPos, deDondeSonLosDelPos),
        ];
        foreach (var parte in partes)
        {
            // Los datos de verdad solo se ven una vez, con el dueño: una parte que no se pueda leer (otra versión de las tablas, un costo que
            // no se entiende) lo dice y las demás siguen.
            try
            {
                parte(conexion, texto);
            }
            catch (Exception ex) when (ex is SqliteException or JsonException or FormatException or InvalidCastException or InvalidOperationException)
            {
                texto.AppendLine();
                texto.AppendLine($"**Esta parte no se pudo leer:** {ex.Message}");
                texto.AppendLine();
            }
        }
        return texto.ToString();
    }

    // ── Las partes ───────────────────────────────────────────────────────────────────────────────────────────────

    private static Completa LeerSiEstaCompleta(SqliteConnection c)
    {
        var datos = Filas(c, "SELECT clave, valor FROM recopilacion WHERE clave IN ('estado', 'motivo', 'avisos')",
            l => (l.GetString(0), l.IsDBNull(1) ? "" : l.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);
        var avisos = datos.GetValueOrDefault("avisos") is { Length: > 2 } json ? JsonSerializer.Deserialize<List<string>>(json) ?? [] : [];
        return new Completa(datos.GetValueOrDefault("estado") == "terminada", datos.GetValueOrDefault("motivo"), avisos);
    }

    private static void Encabezado(SqliteConnection c, StringBuilder t, Completa completa)
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
        if (!completa.Terminada)
        {
            t.AppendLine();
            t.AppendLine($"**La recopilación no terminó**{(completa.Motivo is { Length: > 0 } motivo ? $" ({motivo})" : "")}: lo que sigue es solo lo que alcanzó a "
                + "leer, y donde falta dice «no se sabe». Vuelve a correr `recopilar-control-print` (sigue donde se quedó) antes de sacar conclusiones.");
        }
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
            _ => $"Se imprimió en {colas.Count} colas: {string.Join(", ", colas.Select(x => $"«{x}»"))}. Una cola es de una PC: dos colas pueden ser de la "
                + "misma impresora. En el módulo, cada PC dice de qué impresora es cada una de sus colas (`ColasImpresion` en su caja.json).",
        });
        t.AppendLine();
    }

    private static void CopiasDelPanel(SqliteConnection c, StringBuilder t, Completa completa)
    {
        t.AppendLine("## Copias del panel");
        t.AppendLine();
        var filas = Filas(c, """
            SELECT substr(dia, 1, 7), COUNT(*), SUM(caras), SUM(caras_color), SUM(precio_a_cobrar)
            FROM trabajos WHERE tipo = 'CopiaEnElPanel' GROUP BY 1 ORDER BY 1
            """, l => new[] { l.GetString(0), Numero(l.GetInt64(1)), Numero(l.GetInt64(2)), Numero(l.GetInt64(3)), Pesos(l.GetDouble(4)) });
        if (filas.Count == 0)
        {
            t.AppendLine(completa.Terminada ? "No se registraron copias del panel en Control Print." : $"Copias del panel: {completa.NoSeSabe}");
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

    private static void Cotizaciones(SqliteConnection c, StringBuilder t, Completa completa)
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
            t.AppendLine(completa.SinCotizaciones
                ? $"Cuánto se usó «Guardar cotización»: {completa.NoSeSabe}"
                : "No hay cotizaciones guardadas: en Control Print casi no se usó «Guardar cotización» (o no se guardó ninguna).");
            t.AppendLine();
            return;
        }
        t.AppendLine($"- Guardadas: {Numero(guardadas)} ({Numero(especiales)} con precio especial).");
        t.AppendLine($"- Usadas (ligadas a un trabajo impreso): {Numero(usadas)}, {Porcentaje(usadas, guardadas)}.");
        t.AppendLine($"- Cobradas con el precio cotizado: {Numero(cobradas)}; trabajos con precio «cotizado»: {Numero(conPrecioCotizado)}.");
        t.AppendLine($"- Vencidas sin usarse: {Numero(vencidas)}.");
        t.AppendLine();
    }

    /// <summary>Una cotización de Control Print con lo que se cotizó.</summary>
    private sealed record CotizacionLeida(long Id, DateTime Creada, string Dia, string Archivo, int Paginas, int Copias, int Caras, double Costo,
        double Precio, DateTime ValeHasta, long? TrabajoId, string? Calidad, string? Papel, bool? Color, bool? Duplex);

    /// <summary>Un trabajo impreso de Control Print, con lo que hace falta para compararlo con su cotización.</summary>
    private sealed record TrabajoLeido(long Id, string Documento, DateTime Detectado, int Paginas, int Copias, int Caras, string Calidad, string Papel,
        bool Color, bool Duplex, double Precio, double Costo, double Tinta);

    /// <summary>
    /// Lo cotizado contra lo impreso (2026-11-08; el dueño: «cotizar daba un precio inferior al de la impresión»): cada cotización con su
    /// trabajo (el que ligó Control Print o, si no, el mismo documento con casi las mismas páginas, impreso mientras valía, como lo liga el
    /// módulo), el precio cotizado contra el que calculó la impresión y, de las que se imprimieron igual, la tinta: de ahí sale la corrección
    /// inicial de cotizar. Los documentos no se nombran.
    /// </summary>
    private static void CotizadoContraImpreso(SqliteConnection c, StringBuilder t, Completa completa)
    {
        t.AppendLine("## Lo cotizado contra lo impreso");
        t.AppendLine();
        var cotizaciones = Filas(c, """
            SELECT id, creada_utc, dia, archivo, paginas, copias, caras, costo_total, precio, vale_hasta_utc, trabajo_id, opciones_json FROM cotizaciones
            ORDER BY creada_utc
            """, l =>
        {
            string? Texto(JsonElement o, string nombre) => Propiedad(o, nombre) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
            bool? SiNo(JsonElement o, string nombre) => Propiedad(o, nombre) is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : null;
            var opciones = l.IsDBNull(11) ? default : JsonDocument.Parse(l.GetString(11)).RootElement.Clone();
            return new CotizacionLeida(l.GetInt64(0), Utc(l.GetString(1)), l.GetString(2), l.GetString(3), l.GetInt32(4), l.GetInt32(5), l.GetInt32(6),
                l.GetDouble(7), l.GetDouble(8), Utc(l.GetString(9)), l.IsDBNull(10) ? null : l.GetInt64(10), Texto(opciones, "Quality"),
                Texto(opciones, "MediaType"), SiNo(opciones, "Color"), SiNo(opciones, "Duplex"));
        });
        if (cotizaciones.Count == 0)
        {
            t.AppendLine(completa.SinCotizaciones ? $"Lo cotizado contra lo impreso: {completa.NoSeSabe}" : "No hay cotizaciones guardadas con que comparar.");
            t.AppendLine();
            return;
        }
        var trabajos = Filas(c, """
            SELECT id, documento, detectado_utc, paginas, copias, caras, calidad, tipo_papel, color, doble_cara, precio, costo_total, costo_tinta
            FROM trabajos WHERE tipo = 'Impresion' ORDER BY detectado_utc
            """, l => new TrabajoLeido(l.GetInt64(0), l.GetString(1), Utc(l.GetString(2)), l.GetInt32(3), l.GetInt32(4), l.GetInt32(5), l.GetString(6),
                l.GetString(7), l.GetInt64(8) != 0, l.GetInt64(9) != 0, l.GetDouble(10), l.GetDouble(11), l.GetDouble(12)));
        var porId = trabajos.ToDictionary(x => x.Id);
        var usados = new HashSet<long>(cotizaciones.Where(q => q.TrabajoId is not null).Select(q => q.TrabajoId!.Value));
        var pares = new List<(CotizacionLeida Q, TrabajoLeido J, bool Ligada)>();
        foreach (var q in cotizaciones)
        {
            if (q.TrabajoId is { } id && porId.TryGetValue(id, out var ligado))
            {
                pares.Add((q, ligado, true));
                continue;
            }
            // Como lo liga el módulo (LoCotizado): el mismo documento, casi las mismas páginas, impreso mientras valía y no usado por otra.
            var trabajo = trabajos.FirstOrDefault(j => !usados.Contains(j.Id) && j.Detectado >= q.Creada.AddMinutes(-LoCotizado.MinutosAntes)
                && j.Detectado <= q.ValeHasta && Math.Abs(j.Paginas - q.Paginas) <= LoCotizado.ToleranciaPaginas(q.Paginas)
                && LoCotizado.MismoDocumento(q.Archivo, j.Documento));
            if (trabajo is null)
                continue;
            usados.Add(trabajo.Id);
            pares.Add((q, trabajo, false));
        }
        t.AppendLine($"- Cotizaciones: {Numero(cotizaciones.Count)}; se encontró su impresión en {Numero(pares.Count)} ({Porcentaje(pares.Count, cotizaciones.Count)}): "
            + $"{Numero(pares.Count(p => p.Ligada))} las ligó Control Print y {Numero(pares.Count(p => !p.Ligada))} por el nombre y las páginas.");
        if (pares.Count == 0)
        {
            t.AppendLine();
            return;
        }
        static bool Iguales(CotizacionLeida q, TrabajoLeido j) =>
            q.Caras == j.Caras && q.Copias == j.Copias && (q.Duplex is null || q.Duplex == j.Duplex)
            && (q.Calidad is null || string.Equals(q.Calidad, j.Calidad, StringComparison.OrdinalIgnoreCase))
            && (q.Papel is null || string.Equals(q.Papel, j.Papel, StringComparison.OrdinalIgnoreCase));
        var iguales = pares.Where(p => Iguales(p.Q, p.J)).ToList();
        var cotizado = pares.Sum(p => p.Q.Precio);
        var impreso = pares.Sum(p => p.J.Precio);
        t.AppendLine($"- Precio: se cotizaron {Pesos(cotizado)} y al imprimir se calcularon {Pesos(impreso)} ({Diferencia((decimal)(impreso - cotizado))}); "
            + $"lo impreso salió más caro en {Numero(pares.Count(p => p.J.Precio > p.Q.Precio + 0.005))} de {Numero(pares.Count)}.");
        if (iguales.Count > 0)
        {
            var promedio = iguales.Where(p => p.Q.Precio > 0).Select(p => (p.J.Precio - p.Q.Precio) / p.Q.Precio * 100).DefaultIfEmpty().Average();
            // Con las mismas caras y ajustes, el papel y lo demás cuestan lo mismo: la diferencia del costo es la de la tinta.
            var tintaCotizada = iguales.Sum(p => p.Q.Costo - (p.J.Costo - p.J.Tinta));
            var tintaImpresa = iguales.Sum(p => p.J.Tinta);
            t.AppendLine($"- Impresas igual que lo cotizado (las mismas caras, copias, calidad y papel): {Numero(iguales.Count)}. Lo impreso salió en promedio "
                + $"{promedio:+0.#;-0.#;0} % contra lo cotizado.");
            if (tintaCotizada > 0)
                t.AppendLine($"- Su tinta: cotizada {Pesos(tintaCotizada)}, impresa {Pesos(tintaImpresa)}: la impresa entre la cotizada da "
                    + $"**{tintaImpresa / tintaCotizada:0.00}**, la corrección inicial de cotizar (Configuración › Impresoras e impresiones, «Corrección al "
                    + "cotizar»), si son bastantes pares y de la misma impresora. Los costos pudieron cambiar entre cotizar e imprimir (la calibración).");
        }
        var distintos = pares.Where(p => !Iguales(p.Q, p.J)).ToList();
        if (distintos.Count > 0)
        {
            var calidad = distintos.Count(p => p.Q.Calidad is { } q && !string.Equals(q, p.J.Calidad, StringComparison.OrdinalIgnoreCase));
            var papel = distintos.Count(p => p.Q.Papel is { } q && !string.Equals(q, p.J.Papel, StringComparison.OrdinalIgnoreCase));
            var caras = distintos.Count(p => p.Q.Caras != p.J.Caras || p.Q.Copias != p.J.Copias);
            t.AppendLine($"- Impresas distinto a lo cotizado: {Numero(distintos.Count)} (otra calidad {Numero(calidad)}, otro papel {Numero(papel)}, otras caras o "
                + $"copias {Numero(caras)}). Ahí la diferencia es de lo que se eligió, no de la medición.");
        }
        t.AppendLine();
        var mayores = pares.OrderByDescending(p => Math.Abs(p.J.Precio - p.Q.Precio)).Take(10).ToList();
        Tabla(t, ["Cotización", "Día", "Trabajo", "Caras (cot./imp.)", "Calidad (cot./imp.)", "Cotizado", "Impreso", "Igual"], izquierda: 3, filas: mayores
            .Select(p => new[]
            {
                $"#{p.Q.Id}", p.Q.Dia, $"#{p.J.Id}", $"{p.Q.Caras} / {p.J.Caras}", $"{p.Q.Calidad ?? "—"} / {p.J.Calidad}", Pesos(p.Q.Precio), Pesos(p.J.Precio),
                Iguales(p.Q, p.J) ? "sí" : "no",
            }).ToList());
    }

    /// <summary>
    /// Con qué calidad y papel se imprime de verdad en cada cola (2026-11-08): cotizar con otra calidad cambia la tinta (Control Print cotizaba
    /// en «Standard»; «StandardVivid» lleva un 15 % más y «HighQuality» un 30 %). El módulo cotiza con lo más usado en cada impresora.
    /// </summary>
    private static void CalidadConQueSeImprime(SqliteConnection c, StringBuilder t)
    {
        t.AppendLine("## La calidad y el papel con que se imprime");
        t.AppendLine();
        var filas = Filas(c, """
            SELECT impresora, calidad, tipo_papel, COUNT(*), SUM(caras) FROM trabajos WHERE tipo = 'Impresion'
            GROUP BY 1, 2, 3 ORDER BY 1, 4 DESC
            """, l => new[] { l.GetString(0), l.GetString(1), l.GetString(2), Numero(l.GetInt64(3)), Numero(l.GetInt64(4)) });
        Tabla(t, ["Cola", "Calidad", "Papel", "Trabajos", "Caras"], filas);
        var cotizadas = Filas(c, "SELECT opciones_json FROM cotizaciones WHERE opciones_json IS NOT NULL", l => l.GetString(0))
            .Select(j => Propiedad(JsonDocument.Parse(j).RootElement, "Quality") is { ValueKind: JsonValueKind.String } q ? q.GetString() ?? "—" : "—")
            .GroupBy(q => q).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ({Numero(g.Count())})").ToList();
        if (cotizadas.Count > 0)
            t.AppendLine($"Las cotizaciones se hicieron en: {string.Join(", ", cotizadas)}.");
        t.AppendLine();
    }

    private static JsonElement? Propiedad(JsonElement objeto, string nombre)
    {
        if (objeto.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var p in objeto.EnumerateObject())
            if (string.Equals(p.Name, nombre, StringComparison.OrdinalIgnoreCase))
                return p.Value;
        return null;
    }

    private static DateTime Utc(string texto) =>
        DateTime.Parse(texto, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static void PreciosPuestos(SqliteConnection c, StringBuilder t, Completa completa)
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
            t.AppendLine(completa.Terminada ? "Ningún trabajo con precio puesto: todo se cobró con el calculado." : $"Precios puestos: {completa.NoSeSabe}");
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

    private static void NoVendidas(SqliteConnection c, StringBuilder t, Completa completa)
    {
        t.AppendLine("## No vendidas");
        t.AppendLine();
        var filas = Filas(c, """
            SELECT COALESCE(motivo_no_vendida, 'Desconocido'), COUNT(*), SUM(costo_total), SUM(caras)
            FROM trabajos WHERE cobro = 'NoVendido' GROUP BY 1 ORDER BY 2 DESC
            """, l => new[] { Motivo(l.GetString(0)), Numero(l.GetInt64(1)), Pesos(l.GetDouble(2)), Numero(l.GetInt64(3)) });
        if (filas.Count == 0)
        {
            t.AppendLine(completa.Terminada ? "Ninguna marcada como no vendida." : $"No vendidas: {completa.NoSeSabe}");
            t.AppendLine();
            return;
        }
        Tabla(t, ["Motivo", "Trabajos", "Costo perdido", "Caras"], filas, izquierda: 1);
        var total = Fila(c, "SELECT COUNT(*) FROM trabajos WHERE cobrable = 1 OR cobro = 'NoVendido'", l => l.GetInt64(0));
        var noVendidas = filas.Sum(f => long.Parse(f[1], NumberStyles.AllowThousands, CultureInfo.InvariantCulture));
        t.AppendLine($"Son {Porcentaje(noVendidas, total)} de los trabajos con precio. Las notas no se copian aquí.");
        t.AppendLine();
    }

    private static void HojasYMedicion(SqliteConnection c, StringBuilder t, Completa completa)
    {
        t.AppendLine("## Hojas que faltaron y lo que se midió");
        t.AppendLine();
        var (faltaron, soloLoQueSalio, noVendidas, pendientes) = Fila(c, """
            SELECT COUNT(*), SUM(CASE WHEN tipo_precio = 'SoloLoQueSalio' THEN 1 ELSE 0 END), SUM(CASE WHEN cobro = 'NoVendido' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN cobro = 'PorCobrar' THEN 1 ELSE 0 END)
            FROM trabajos WHERE faltaron_caras = 1
            """, l => (l.GetInt64(0), l.IsDBNull(1) ? 0 : l.GetInt64(1), l.IsDBNull(2) ? 0 : l.GetInt64(2), l.IsDBNull(3) ? 0 : l.GetInt64(3)));
        t.AppendLine(faltaron == 0
            ? completa.Terminada ? "- Ningún trabajo con hojas que faltaron (la impresora contó todas)." : $"- Hojas que faltaron: {completa.NoSeSabe}"
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
        var iguales = calculados.Count(x => Igual(x.Modulo.Price, x.Precio));
        var sumaControlPrint = calculados.Sum(x => x.Precio);
        var sumaCobrado = calculados.Sum(x => x.Cobrado);
        var sumaModulo = calculados.Sum(x => x.Modulo.Price);
        t.AppendLine($"### {titulo}");
        t.AppendLine();
        t.AppendLine($"- El mismo precio que calculó Control Print: {Numero(iguales)} de {Numero(calculados.Count)} ({Porcentaje(iguales, calculados.Count)}).");
        t.AppendLine($"- En total: Control Print calculó {Pesos(sumaControlPrint)} y cobra {Pesos(sumaCobrado)} (con los precios puestos); el módulo, "
            + $"{Pesos(sumaModulo)} ({Diferencia(sumaModulo - sumaControlPrint)} contra lo calculado).");
        t.AppendLine($"- {nota}");
        var distintos = calculados.Where(x => !Igual(x.Modulo.Price, x.Precio)).OrderByDescending(x => Math.Abs(x.Modulo.Price - x.Precio)).Take(10).ToList();
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

    /// <summary>El mismo precio, sin contar menos de medio centavo (en la recopilación los pesos van como número de punto flotante).</summary>
    private static bool Igual(decimal a, decimal b) => Math.Abs(a - b) < 0.005m;

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
