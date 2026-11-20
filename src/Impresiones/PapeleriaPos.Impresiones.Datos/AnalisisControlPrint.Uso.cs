using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Impresiones.Datos;

/// <summary>Un renglón de las ventas de eleventa por día y producto (IMP, COP…), para cruzarlas con Control Print.</summary>
public sealed record VentaDeEleventa(DateOnly Dia, string Codigo, int Renglones, decimal Cantidad, decimal Importe);

/// <summary>
/// La fase 1 del itinerario de los datos de Control Print (2026-11-20): por qué no coincide el precio, la tinta que bajó de verdad, lo que
/// contó la impresora sin registrarse, cuándo se imprime y lo cobrado en eleventa. Todo solo leyendo, sin documentos ni clientes.
/// </summary>
public static partial class AnalisisControlPrint
{
    private static readonly string[] Dias = ["Domingo", "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado"];

    /// <summary>Lo que guarda Control Print del cálculo de cada trabajo: con qué calibración y en cuánto salió cada parte.</summary>
    private sealed record CostoGuardado(double? CalC, double? CalM, double? CalY, double? CalK, double? InkC, double? InkM, double? InkY,
        double? InkK, int? Sides, int? ColorSides, decimal? InkCost, decimal? PaperCost, decimal? MaintenanceCost, decimal? MarginPercent,
        decimal? Price);

    /// <summary>
    /// Las ventas de eleventa que deja <c>scripts/ventas-impresiones-eleventa.sql</c> (con isql): renglones «día,código,renglones,cantidad,importe».
    /// Lo demás (los encabezados y los espacios de isql) se salta.
    /// </summary>
    public static IReadOnlyList<VentaDeEleventa> LeerVentasDeEleventa(string archivo)
    {
        var ventas = new List<VentaDeEleventa>();
        foreach (var linea in File.ReadLines(archivo))
        {
            var partes = linea.Trim().Split(',', StringSplitOptions.TrimEntries);
            if (partes.Length != 5
                || !DateOnly.TryParseExact(partes[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dia)
                || partes[1].Length == 0
                || !int.TryParse(partes[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var renglones)
                || !decimal.TryParse(partes[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var cantidad)
                || !decimal.TryParse(partes[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var importe))
                continue;
            ventas.Add(new VentaDeEleventa(dia, partes[1].ToUpperInvariant(), renglones, cantidad, Math.Round(importe, 2)));
        }
        return ventas;
    }

    // ── 1.1 Por qué no coincide el precio ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Control Print recalibra la tinta cada pocos días y cada trabajo guarda la calibración con que salió: el módulo, con la calibración
    /// de hoy, da otro precio. Se vuelven a calcular los que no coinciden con la de su día: si así coinciden, era solo eso; los que siguen
    /// sin coincidir dicen en qué parte se alejan.
    /// </summary>
    private static void PorQueNoCoincide(StringBuilder t, CostSettings costos,
        List<(long Id, string Dia, int Caras, decimal Precio, decimal Cobrado, decimal Costo, JobAnalysis Analisis, CostoGuardado? Guardado)> trabajos)
    {
        t.AppendLine("### Por qué no coincide");
        t.AppendLine();
        var conSuCalibracion = new Dictionary<(double, double, double, double), CostSettings>();
        CostSettings ConCalibracion(CostoGuardado g)
        {
            var clave = (g.CalC ?? 1, g.CalM ?? 1, g.CalY ?? 1, g.CalK ?? 1);
            if (!conSuCalibracion.TryGetValue(clave, out var copia))
            {
                copia = costos.Clone();
                copia.Calibration.Enabled = true;
                (copia.Calibration.C, copia.Calibration.M, copia.Calibration.Y, copia.Calibration.K) = clave;
                conSuCalibracion[clave] = copia;
            }
            return copia;
        }
        var filas = trabajos.Select(x =>
        {
            var hoy = CostCalculator.Calculate(x.Analisis, costos);
            var suyo = x.Guardado is { CalC: not null } g ? CostCalculator.Calculate(x.Analisis, ConCalibracion(g)) : null;
            return (x.Id, x.Dia, x.Precio, x.Guardado, Hoy: hoy, Suyo: suyo);
        }).ToList();
        var igualesHoy = filas.Count(x => Igual(x.Hoy.Price, x.Precio));
        var igualesConLaSuya = filas.Count(x => !Igual(x.Hoy.Price, x.Precio) && x.Suyo is { } s && Igual(s.Price, x.Precio));
        var sinCalibracion = filas.Count(x => !Igual(x.Hoy.Price, x.Precio) && x.Suyo is null);
        var siguen = filas.Where(x => !Igual(x.Hoy.Price, x.Precio) && x.Suyo is { } s && !Igual(s.Price, x.Precio)).ToList();
        t.AppendLine($"- Coinciden con la calibración de hoy: {Numero(igualesHoy)} de {Numero(filas.Count)} ({Porcentaje(igualesHoy, filas.Count)}).");
        t.AppendLine($"- Coinciden con la calibración con que salieron (Control Print la cambia cada pocos días): {Numero(igualesConLaSuya)} más. Con esas, "
            + $"{Numero(igualesHoy + igualesConLaSuya)} de {Numero(filas.Count)} ({Porcentaje(igualesHoy + igualesConLaSuya, filas.Count)}) dan el mismo precio.");
        if (sinCalibracion > 0)
            t.AppendLine($"- Sin la calibración guardada (no se puede saber): {Numero(sinCalibracion)}.");
        t.AppendLine($"- Siguen sin coincidir: {Numero(siguen.Count)}" + (siguen.Count == 0
            ? ". El cálculo del módulo es el de Control Print."
            : $" ({Porcentaje(siguen.Count, filas.Count)}). Es otro costo que cambió o una diferencia del cálculo: la parte que se aleja va en la tabla. "
              + "«Mantenimiento» es la caja de mantenimiento, cuyas caras por caja Control Print también vuelve a medir: es un costo que cambió, no el cálculo."));
        t.AppendLine();
        if (siguen.Count > 0)
        {
            static string Partes(CostoGuardado? g, CostBreakdown m)
            {
                if (g is null)
                    return "—";
                var partes = new List<string>();
                if (g.InkCost is { } tinta && Math.Abs(tinta - m.InkCost) >= 0.005m) partes.Add("tinta");
                if (g.PaperCost is { } papel && Math.Abs(papel - m.PaperCost) >= 0.005m) partes.Add("papel");
                if (g.MaintenanceCost is { } caja && Math.Abs(caja - m.MaintenanceCost) >= 0.005m) partes.Add("mantenimiento");
                if (g.MarginPercent is { } margen && Math.Abs(margen - m.MarginPercent) >= 0.5m) partes.Add("margen");
                return partes.Count == 0 ? "redondeo u otro" : string.Join(", ", partes);
            }
            var porParte = siguen.GroupBy(x => Partes(x.Guardado, x.Suyo!)).OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key} ({Numero(g.Count())})").ToList();
            t.AppendLine($"En qué se alejan: {string.Join(", ", porParte)}.");
            t.AppendLine();
            Tabla(t, ["Trabajo", "Día", "Control Print", "Módulo con su calibración", "Se aleja en"], izquierda: 2, filas: siguen
                .OrderByDescending(x => Math.Abs(x.Suyo!.Price - x.Precio)).Take(10)
                .Select(x => new[] { $"#{x.Id}", x.Dia, Pesos(x.Precio), Pesos(x.Suyo!.Price), Partes(x.Guardado, x.Suyo!) }).ToList());
        }
        var porSemana = filas.GroupBy(x => Semana(x.Dia)).OrderBy(g => g.Key).Select(g => new[]
        {
            g.Key, Numero(g.Count()), Porcentaje(g.Count(x => Igual(x.Hoy.Price, x.Precio)), g.Count()),
            Porcentaje(g.Count(x => Igual(x.Hoy.Price, x.Precio) || x.Suyo is { } s && Igual(s.Price, x.Precio)), g.Count()),
        }).ToList();
        t.AppendLine("Por semana (desde el lunes):");
        t.AppendLine();
        Tabla(t, ["Semana", "Trabajos", "Iguales con la calibración de hoy", "Iguales con la suya"], porSemana, izquierda: 1);
    }

    // ── 1.2 La tinta que bajó de verdad ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// La calibración de Control Print, vuelta a calcular con el del módulo (Calibrator, el mismo de Control Print) sobre las lecturas de la
    /// impresora: cuánto bajó cada cartucho contra lo que estimó el modelo, el factor que se aplica (con su tope de 0.4 a 2.5) y cuánto
    /// cambiaría el precio si se cobrara la tinta como bajó de verdad.
    /// </summary>
    private static void Tinta(SqliteConnection c, StringBuilder t)
    {
        t.AppendLine("## La tinta: lo que estimó el modelo contra lo que bajó");
        t.AppendLine();
        var costos = CostosDeControlPrint(c);
        var lecturas = Lecturas(c);
        if (costos is null || lecturas.Count < 2)
        {
            t.AppendLine(costos is null ? "La recopilación no trae los costos de Control Print." : "No hay lecturas de la impresora para comparar.");
            t.AppendLine();
            return;
        }
        var trabajos = TrabajosParaCalibrar(c, costos);
        var calibracion = Calibrator.Calibrate(lecturas, trabajos.Select(x => x.Trabajo).ToList(), costos);
        var deControlPrint = CalibracionGuardada(c);
        t.AppendLine($"Con {Numero(lecturas.Count)} lecturas de la impresora (del {Local(calibracion.FromUtc)} al {Local(calibracion.ToUtc)}) y "
            + $"{Numero(trabajos.Count)} trabajos impresos. Los tramos en que se cambió un cartucho no cuentan; lo que la impresora imprimió sin pasar por "
            + "Control Print (copias, celular) se estima con lo que gasta en promedio una cara registrada.");
        t.AppendLine();
        Tabla(t, ["Tinta", "Bajó (puntos)", "El modelo estimó", "Real ÷ estimado", "Factor del módulo", "El que aplica Control Print"], izquierda: 1,
            filas: calibracion.Channels.Select(ch => new[]
            {
                Calibrator.Name(ch.Channel), ch.ObservedPoints.ToString("0.#", CultureInfo.InvariantCulture),
                ch.EstimatedPoints.ToString("0.#", CultureInfo.InvariantCulture),
                ch.Ratio is { } r ? r.ToString("0.00", CultureInfo.InvariantCulture) : "—", ch.Factor.ToString("0.00", CultureInfo.InvariantCulture),
                costos.Calibration.Enabled ? costos.Calibration.Factor(ch.Channel).ToString("0.00", CultureInfo.InvariantCulture) : "apagada",
            }).ToList());
        // El tope de Control Print (el módulo lo amplía con datos confiables; docs/impresion.md, «La calibración sin el tope de 2.5»).
        var topados = calibracion.Channels.Where(ch => ch.Ratio is { } r && (r > 2.5 || r < 0.4)).ToList();
        if (topados.Count > 0)
            t.AppendLine($"**El tope de Control Print esconde parte de la diferencia** en {string.Join(", ", topados.Select(ch => Calibrator.Name(ch.Channel).ToLowerInvariant()))}: "
                + "su calibración no pasa de 0.4 a 2.5, y lo que bajó de verdad está fuera de eso.");
        if (deControlPrint is { } guardada && guardada.Count > 0)
            t.AppendLine($"Control Print calculó la suya el {guardada.First().Calculada}: "
                + string.Join(", ", guardada.Select(x => $"{x.Canal} {x.Razon:0.00}")) + " (real ÷ estimado).");
        var cambios = calibracion.Changes.Where(x => x.Supply != "Caja de mantenimiento").ToList();
        t.AppendLine($"Cambios de cartucho en el periodo: {Numero(cambios.Count)}"
            + (cambios.Count == 0 ? "." : $" ({string.Join(", ", cambios.GroupBy(x => x.Supply).Select(g => $"{g.Key.ToLowerInvariant()} {g.Count()}"))})."));
        if (calibracion.MaintenancePagesPerBox is { } porCaja)
            t.AppendLine($"La caja de mantenimiento: unas {Numero(porCaja)} caras por caja, medidas (en los costos de Control Print: "
                + $"{Numero(costos.Maintenance.PagesPerBox)}).");
        t.AppendLine();

        // Lo que cambiaría el precio: los mismos trabajos con el factor de hoy contra el que dice lo que bajó (sin tope).
        var conDesglose = trabajos.Where(x => x.Analisis is not null).ToList();
        if (conDesglose.Count == 0 || calibracion.Channels.All(ch => ch.Ratio is null))
            return;
        var sinTope = costos.Clone();
        sinTope.Calibration.Enabled = true;
        double Razon(InkChannel canal, double siNo) => calibracion.Channels.First(x => x.Channel == canal).Ratio ?? siNo;
        sinTope.Calibration.C = Razon(InkChannel.Cyan, costos.Calibration.C);
        sinTope.Calibration.M = Razon(InkChannel.Magenta, costos.Calibration.M);
        sinTope.Calibration.Y = Razon(InkChannel.Yellow, costos.Calibration.Y);
        sinTope.Calibration.K = Razon(InkChannel.Black, costos.Calibration.K);
        var hoy = conDesglose.Sum(x => CostCalculator.Calculate(x.Analisis!, costos).Price);
        var real = conDesglose.Sum(x => CostCalculator.Calculate(x.Analisis!, sinTope).Price);
        var tintaHoy = conDesglose.Sum(x => CostCalculator.Calculate(x.Analisis!, costos).InkCost);
        var tintaReal = conDesglose.Sum(x => CostCalculator.Calculate(x.Analisis!, sinTope).InkCost);
        t.AppendLine($"Si la tinta se cobrara como bajó de verdad (el real ÷ estimado de arriba, sin el tope de Control Print), los mismos {Numero(conDesglose.Count)} trabajos: "
            + $"tinta {Pesos(tintaReal)} en lugar de {Pesos(tintaHoy)}, y precio {Pesos(real)} en lugar de {Pesos(hoy)} ({Diferencia(real - hoy)}).");
        t.AppendLine();
    }

    // ── 1.3 Lo que contó la impresora y no se registró ────────────────────────────────────────────────────────────

    /// <summary>
    /// El contador de la impresora contra las caras registradas, entre cada par de lecturas: lo que contó de más son las copias del panel,
    /// lo del celular y lo que se imprimió desde una PC sin Control Print. Es lo que el módulo convierte en tarjetas «Copias o celular».
    /// </summary>
    private static void LoQueContoLaImpresora(SqliteConnection c, StringBuilder t)
    {
        t.AppendLine("## Lo que contó la impresora y no se registró");
        t.AppendLine();
        var lecturas = Lecturas(c).Where(x => x.ColorCount is not null && x.MonoCount is not null).ToList();
        if (lecturas.Count < 2)
        {
            t.AppendLine("No hay lecturas con los contadores de color y B/N.");
            t.AppendLine();
            return;
        }
        var series = Filas(c, "SELECT DISTINCT serie FROM lecturas_impresora WHERE serie IS NOT NULL AND serie <> ''", l => l.GetString(0));
        t.AppendLine(series.Count switch
        {
            0 => "Las lecturas no traen la serie de la impresora: son de la que vigila Control Print (hay que confirmar en la tienda cuál es).",
            1 => $"Las lecturas son de la impresora con serie {series[0]}.",
            _ => $"**Las lecturas traen {series.Count} series distintas:** los contadores de impresoras distintas no se pueden restar; esta parte no sirve así.",
        });
        t.AppendLine();
        if (series.Count > 1)
            return;
        var trabajos = Filas(c, """
            SELECT COALESCE(terminado_utc, detectado_utc), caras, caras_color FROM trabajos
            WHERE tipo = 'Impresion' AND estado = 'Impreso' ORDER BY 1
            """, l => (Utc: Utc(l.GetString(0)), Caras: l.GetInt64(1), Color: l.GetInt64(2)));
        var semanas = new SortedDictionary<string, (long Color, long Mono, long RegColor, long RegMono, int Dias)>(StringComparer.Ordinal);
        var diasConLecturas = new HashSet<DateOnly>();
        var siguiente = 0;
        for (var i = 1; i < lecturas.Count; i++)
        {
            var (a, b) = (lecturas[i - 1], lecturas[i]);
            while (siguiente < trabajos.Count && trabajos[siguiente].Utc <= a.TakenUtc)
                siguiente++;
            long regColor = 0, regMono = 0;
            for (var j = siguiente; j < trabajos.Count && trabajos[j].Utc <= b.TakenUtc; j++)
            {
                regColor += trabajos[j].Color;
                regMono += trabajos[j].Caras - trabajos[j].Color;
            }
            var dia = DateOnly.FromDateTime(HoraMexico.ALocal(b.TakenUtc));
            diasConLecturas.Add(dia);
            var semana = Semana(dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var s = semanas.GetValueOrDefault(semana);
            semanas[semana] = (s.Color + Math.Max(0, b.ColorCount!.Value - a.ColorCount!.Value), s.Mono + Math.Max(0, b.MonoCount!.Value - a.MonoCount!.Value),
                s.RegColor + regColor, s.RegMono + regMono, s.Dias);
        }
        foreach (var dia in diasConLecturas)
        {
            var semana = Semana(dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var s = semanas[semana];
            semanas[semana] = s with { Dias = s.Dias + 1 };
        }
        var totalColor = semanas.Values.Sum(x => x.Color);
        var totalMono = semanas.Values.Sum(x => x.Mono);
        var regColorTotal = semanas.Values.Sum(x => x.RegColor);
        var regMonoTotal = semanas.Values.Sum(x => x.RegMono);
        var sinColor = Math.Max(0, totalColor - regColorTotal);
        var sinMono = Math.Max(0, totalMono - regMonoTotal);
        t.AppendLine($"- La impresora contó {Numero(totalColor)} caras a color y {Numero(totalMono)} en B/N; Control Print registró {Numero(regColorTotal)} y "
            + $"{Numero(regMonoTotal)}.");
        t.AppendLine($"- Sin registrar: {Numero(sinColor)} a color ({Porcentaje(sinColor, totalColor)}) y {Numero(sinMono)} en B/N ({Porcentaje(sinMono, totalMono)}); "
            + $"en {Numero(diasConLecturas.Count)} días con lecturas, unas {Numero(sinMono / Math.Max(1, diasConLecturas.Count))} caras B/N y "
            + $"{Numero(sinColor / Math.Max(1, diasConLecturas.Count))} a color al día.");
        t.AppendLine("- Lo sin registrar es lo que el módulo vuelve tarjetas «Copias o celular · N caras» con el contador. Una cara B/N de más también puede ser "
            + "una página que el driver mandó en color y la impresora contó en B/N (o al revés): por eso se ve por semana.");
        t.AppendLine();
        Tabla(t, ["Semana", "Días con lecturas", "Contó color", "Registró color", "Contó B/N", "Registró B/N", "Sin registrar B/N"], izquierda: 1, filas: semanas
            .Select(x => new[]
            {
                x.Key, Numero(x.Value.Dias), Numero(x.Value.Color), Numero(x.Value.RegColor), Numero(x.Value.Mono), Numero(x.Value.RegMono),
                Numero(Math.Max(0, x.Value.Mono - x.Value.RegMono)),
            }).ToList());
    }

    // ── 1.5 Cuándo se imprime ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Por día de la semana y por hora (de la tienda), y los 15 minutos con más trabajos: lo que tienen que aguantar la cola y el agente.</summary>
    private static void CuandoSeImprime(SqliteConnection c, StringBuilder t)
    {
        t.AppendLine("## Cuándo se imprime");
        t.AppendLine();
        var trabajos = Filas(c, "SELECT dia, hora_local, caras FROM trabajos WHERE tipo = 'Impresion' AND estado = 'Impreso' AND hora_local IS NOT NULL",
            l => (Dia: DateOnly.ParseExact(l.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Hora: DateTime.ParseExact(l.GetString(1), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), Caras: l.GetInt64(2)));
        if (trabajos.Count == 0)
        {
            t.AppendLine("No hay trabajos impresos.");
            t.AppendLine();
            return;
        }
        var desde = trabajos.Min(x => x.Dia);
        var hasta = trabajos.Max(x => x.Dia);
        int Cuantos(DayOfWeek d)
        {
            var n = 0;
            for (var dia = desde; dia <= hasta; dia = dia.AddDays(1))
                if (dia.DayOfWeek == d) n++;
            return Math.Max(1, n);
        }
        Tabla(t, ["Día", "Trabajos", "Caras", "Trabajos por día", "Caras por día"], izquierda: 1, filas: Enumerable.Range(1, 7).Select(i => (DayOfWeek)(i % 7))
            .Select(d =>
            {
                var delDia = trabajos.Where(x => x.Dia.DayOfWeek == d).ToList();
                var n = Cuantos(d);
                return new[]
                {
                    Dias[(int)d], Numero(delDia.Count), Numero(delDia.Sum(x => x.Caras)), Numero((long)Math.Round(delDia.Count / (double)n)),
                    Numero((long)Math.Round(delDia.Sum(x => x.Caras) / (double)n)),
                };
            }).ToList());
        var diasConTrabajo = trabajos.Select(x => x.Dia).Distinct().Count();
        Tabla(t, ["Hora", "Trabajos", "Caras", "Trabajos por día"], izquierda: 1, filas: trabajos.GroupBy(x => x.Hora.Hour).OrderBy(g => g.Key)
            .Select(g => new[]
            {
                $"{g.Key:00}:00", Numero(g.Count()), Numero(g.Sum(x => x.Caras)), (g.Count() / (double)diasConTrabajo).ToString("0.#", CultureInfo.InvariantCulture),
            }).ToList());
        // Los 15 minutos con más trabajos (una ventana que se recorre).
        var horas = trabajos.Select(x => x.Hora).OrderBy(x => x).ToList();
        var (mejor, inicio) = (0, horas[0]);
        for (int i = 0, j = 0; i < horas.Count; i++)
        {
            while (horas[i] - horas[j] > TimeSpan.FromMinutes(15))
                j++;
            if (i - j + 1 > mejor)
                (mejor, inicio) = (i - j + 1, horas[j]);
        }
        t.AppendLine($"Los 15 minutos con más trabajos: {Numero(mejor)}, desde el {inicio:yyyy-MM-dd HH:mm} ({Dias[(int)inicio.DayOfWeek].ToLowerInvariant()}).");
        t.AppendLine();
    }

    // ── 1.4 Lo cobrado en eleventa ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lo que se cobró en eleventa por impresiones y copias contra lo que calculó Control Print, por semana, en los días que están en las dos.
    /// El último día de las ventas no cuenta: la copia de eleventa se sacó ese día y puede estar a medias.
    /// </summary>
    private static void ContraEleventa(SqliteConnection c, StringBuilder t, IReadOnlyList<VentaDeEleventa> ventas)
    {
        t.AppendLine("## Lo cobrado en eleventa contra lo que calculó Control Print");
        t.AppendLine();
        var deControlPrint = Filas(c, """
            SELECT dia, SUM(precio_a_cobrar), SUM(caras) FROM trabajos WHERE tipo = 'Impresion' AND estado = 'Impreso' AND cobrable = 1 GROUP BY dia
            """, l => (Dia: DateOnly.ParseExact(l.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture), Precio: (decimal)l.GetDouble(1), Caras: l.GetInt64(2)))
            .ToDictionary(x => x.Dia);
        if (ventas.Count == 0 || deControlPrint.Count == 0)
        {
            t.AppendLine(ventas.Count == 0 ? "El archivo de ventas de eleventa no trae renglones." : "No hay trabajos impresos con que comparar.");
            t.AppendLine();
            return;
        }
        var ultimo = ventas.Max(x => x.Dia);
        var desde = new[] { ventas.Min(x => x.Dia), deControlPrint.Keys.Min() }.Max();
        var hasta = new[] { ultimo.AddDays(-1), deControlPrint.Keys.Max() }.Min();
        if (hasta < desde)
        {
            t.AppendLine($"Las ventas de eleventa (del {ventas.Min(x => x.Dia):yyyy-MM-dd} al {ultimo:yyyy-MM-dd}) no se cruzan con los días de Control Print.");
            t.AppendLine();
            return;
        }
        var codigos = ventas.Select(x => x.Codigo).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        t.AppendLine($"Del {desde:yyyy-MM-dd} al {hasta:yyyy-MM-dd} (el {ultimo:yyyy-MM-dd} no cuenta: la copia de eleventa es de ese día). En eleventa: "
            + $"{string.Join(", ", codigos)}, de los tickets no cancelados.");
        t.AppendLine();
        var semanas = new SortedDictionary<string, Dictionary<string, decimal>>(StringComparer.Ordinal);
        var precioCp = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        for (var dia = desde; dia <= hasta; dia = dia.AddDays(1))
        {
            var semana = Semana(dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (!semanas.TryGetValue(semana, out var porCodigo))
                semanas[semana] = porCodigo = codigos.ToDictionary(x => x, _ => 0m);
            foreach (var venta in ventas.Where(x => x.Dia == dia))
                porCodigo[venta.Codigo] += venta.Importe;
            precioCp[semana] = precioCp.GetValueOrDefault(semana) + (deControlPrint.TryGetValue(dia, out var cp) ? cp.Precio : 0);
        }
        Tabla(t, ["Semana", .. codigos.Select(x => $"{x} en eleventa"), "Control Print calculó", "eleventa ÷ Control Print"], izquierda: 1, filas: semanas
            .Select(s =>
            {
                var total = s.Value.Values.Sum();
                var cp = precioCp[s.Key];
                return (string[])[s.Key, .. codigos.Select(x => Pesos(s.Value[x])), Pesos(cp), cp > 0 ? (total / cp).ToString("0.00", CultureInfo.InvariantCulture) : "—"];
            }).ToList());
        var totales = codigos.ToDictionary(x => x, x => semanas.Values.Sum(s => s[x]));
        var totalEleventa = totales.Values.Sum();
        var totalCp = precioCp.Values.Sum();
        t.AppendLine($"- En total: en eleventa {Pesos(totalEleventa)} ({string.Join(", ", codigos.Select(x => $"{x} {Pesos(totales[x])}"))}); Control Print calculó "
            + $"{Pesos(totalCp)} de lo que pasó por él ({Diferencia(totalEleventa - totalCp)} en eleventa).");
        t.AppendLine("- Lo de más en eleventa es lo que se imprimió sin pasar por Control Print (el celular, otra impresora, las copias si se cobran como "
            + "impresión) y **los otros servicios que se cobran con IMP y llevan un costo extra** (el dueño, 2026-11-20): no mide el margen. Lo de "
            + "menos, lo que no se cobró o se cobró con otro producto.");
        t.AppendLine();
    }

    // ── Ayudas de estas partes ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>El lunes de la semana del día («2026-10-05»).</summary>
    private static string Semana(string dia)
    {
        var fecha = DateOnly.ParseExact(dia, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return fecha.AddDays(-(((int)fecha.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string Local(DateTime? utc) => utc is { } u ? HoraMexico.ALocal(u).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "—";

    private static CostSettings? CostosDeControlPrint(SqliteConnection c) =>
        Filas(c, "SELECT json FROM configuracion WHERE clave = 'costos'", l => l.IsDBNull(0) ? null : l.GetString(0)).FirstOrDefault() is { } json
            ? JsonSerializer.Deserialize<CostSettings>(json, Json)
            : null;

    private static List<PrinterStatus> Lecturas(SqliteConnection c) => Filas(c, """
        SELECT tomada_utc, tinta_k, tinta_c, tinta_m, tinta_y, caja_mantenimiento, contador_total, contador_color, contador_bn
        FROM lecturas_impresora ORDER BY tomada_utc
        """, l =>
    {
        int? Entero(int i) => l.IsDBNull(i) ? null : l.GetInt32(i);
        long? Largo(int i) => l.IsDBNull(i) ? null : l.GetInt64(i);
        return new PrinterStatus
        {
            TakenUtc = Utc(l.GetString(0)), InkK = Entero(1), InkC = Entero(2), InkM = Entero(3), InkY = Entero(4), Maintenance = Entero(5),
            LifeCount = Largo(6), ColorCount = Largo(7), MonoCount = Largo(8),
        };
    });

    /// <summary>Los trabajos impresos como los toma la calibración (la tinta del modelo, sin calibrar), con su análisis si traen el desglose.</summary>
    private static List<(CalibrationJob Trabajo, JobAnalysis? Analisis)> TrabajosParaCalibrar(SqliteConnection c, CostSettings costos) => Filas(c, """
        SELECT COALESCE(terminado_utc, detectado_utc), caras, caras_color, desglose_json, nivel_analisis FROM trabajos
        WHERE tipo = 'Impresion' AND estado = 'Impreso'
        """, l =>
    {
        var cuando = Utc(l.GetString(0));
        var caras = l.GetInt32(1);
        var color = l.GetInt32(2);
        Desglose? d = null;
        if (!l.IsDBNull(3))
            try
            {
                d = JsonSerializer.Deserialize<Desglose>(l.GetString(3), Json);
            }
            catch (JsonException)
            {
            }
        JobAnalysis? analisis = d is { Settings: not null, Pages.Count: > 0 }
            ? new JobAnalysis(d.Settings, d.Pages, !l.IsDBNull(4) && l.GetString(4) == "Medido" ? AnalysisLevel.Measured : AnalysisLevel.Estimated,
                null, null, TimeSpan.Zero)
            : null;
        // La tinta del modelo: la que guardó Control Print o, si no, la que da el cálculo (no depende de la calibración).
        var tinta = d?.Cost is { InkC: not null } g
            ? (C: g.InkC ?? 0, M: g.InkM ?? 0, Y: g.InkY ?? 0, K: g.InkK ?? 0)
            : analisis is not null && CostCalculator.Calculate(analisis, costos) is var m ? (C: m.InkC, M: m.InkM, Y: m.InkY, K: m.InkK) : (0, 0, 0, 0);
        return (new CalibrationJob(cuando, caras, color, tinta.C, tinta.M, tinta.Y, tinta.K), analisis);
    });

    /// <summary>La calibración que guardó Control Print (real ÷ estimado por tinta), si viene en la recopilación.</summary>
    private static List<(string Canal, double Razon, string Calculada)>? CalibracionGuardada(SqliteConnection c)
    {
        if (Filas(c, "SELECT json FROM configuracion WHERE clave = 'calibracion'", l => l.IsDBNull(0) ? null : l.GetString(0)).FirstOrDefault() is not { } json)
            return null;
        using var documento = JsonDocument.Parse(json);
        var raiz = documento.RootElement;
        var calculada = Propiedad(raiz, "computedUtc") is { ValueKind: JsonValueKind.String } v && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var cuando)
            ? HoraMexico.ALocal(cuando).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "—";
        if (Propiedad(raiz, "channels") is not { ValueKind: JsonValueKind.Array } canales)
            return null;
        var lista = new List<(string, double, string)>();
        foreach (var canal in canales.EnumerateArray())
            if (Propiedad(canal, "channel") is { ValueKind: JsonValueKind.String } nombre && Propiedad(canal, "ratio") is { ValueKind: JsonValueKind.Number } razon
                && Enum.TryParse<InkChannel>(nombre.GetString(), out var ch))
                lista.Add((Calibrator.Name(ch).ToLowerInvariant(), razon.GetDouble(), calculada));
        return lista;
    }
}
