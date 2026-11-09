using System.Globalization;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Medicion;

namespace PapeleriaPos.Impresiones;

/// <summary>
/// Los costos de impresión de la sucursal (<see cref="CostSettings"/>, los de Control Print) en Configuración › Impresoras e
/// impresiones (2026-10-31): lo principal, en español, para verlos y cambiarlos (<see cref="CostosImpresionDto"/>); lo demás se queda
/// como está. Antes nada en el POS los cambiaba: eran los de Control Print (la caja 2) o los de fábrica, con el papel en cero.
/// </summary>
public static class CostosEnLaConfiguracion
{
    /// <summary>Con Control Print configurado, sus costos mandan: se guardan solos al leerlos y aquí solo se ven.</summary>
    public const string VienenDeControlPrint =
        "Los costos vienen de Control Print: se leen de allá y se guardan solos. Se cambian en Control Print, en Ajustes → Costos.";

    public const decimal PrecioMaximo = 100_000m;
    public const int RendimientoMaximo = 1_000_000;
    public const int HojasMaximas = 100_000;
    public const decimal MargenMaximo = 1_000m;
    public const decimal RedondeoMaximo = 100m;

    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>Las tintas en el orden en que se ven: negro, cian, magenta y amarillo.</summary>
    private static readonly InkChannel[] Tintas = [InkChannel.Black, InkChannel.Cyan, InkChannel.Magenta, InkChannel.Yellow];

    public static CostosImpresionDto De(CostSettings c) => new(
        Tintas.Select(t => new CartuchoCostoDto(Calibrator.Name(t), c.Ink(t).Price, c.Ink(t).IsoYield)).ToList(),
        c.Papers.Select(p => new PapelCostoDto(p.Size.ToString(), p.Group, p.PackPrice, p.PackSheets)).ToList(),
        c.Maintenance.BoxPrice,
        Modo(c.Scaling), c.MarginPercent, Tramos(c.JobCostTiers), Tramos(c.SheetCostTiers), c.MinimumPrice, c.MinimumApplies == MinimumScope.AllJobs,
        c.RoundTo, c.PaperMarginPercent, Curvas(c.Curves));

    private static MargenDeImpresiones Modo(ScalingMode modo) => modo switch
    {
        ScalingMode.ByJobCost => MargenDeImpresiones.PorCostoDelTrabajo,
        ScalingMode.BySheetCost => MargenDeImpresiones.PorCostoDeLaHoja,
        ScalingMode.ByCurves => MargenDeImpresiones.PorCurvas,
        _ => MargenDeImpresiones.Unico,
    };

    public static CurvasMargenDto Curvas(MarginCurves c) => new(
        c.ByCoverage.OrderBy(p => p.X).Select(p => new PuntoCurvaDto(p.X, p.Y)).ToList(),
        c.BySides.OrderBy(p => p.X).Select(p => new PuntoCurvaDto(p.X, p.Y)).ToList());

    /// <summary>Los márgenes de unos costos (los de una impresora con los suyos encima), en español.</summary>
    public static MargenesImpresionDto Margenes(CostSettings c) => new(
        Modo(c.Scaling), c.MarginPercent, Tramos(c.JobCostTiers), Tramos(c.SheetCostTiers), c.PaperMarginPercent, Curvas(c.Curves));

    /// <summary>Los márgenes propios de una impresora, en español.</summary>
    public static MargenesImpresionDto Margenes(MargenesDeImpresora m)
    {
        var c = new CostSettings();
        m.Poner(c);
        return Margenes(c);
    }

    /// <summary>
    /// Los márgenes propios de una impresora como los pide la Configuración, revisados como los de la sucursal (<see cref="Aplicar"/>) sobre
    /// <paramref name="actuales"/> (los costos con que cobra hoy).
    /// </summary>
    public static MargenesDeImpresora AplicarMargenes(CostSettings actuales, MargenesImpresionDto pedidos)
    {
        var dto = De(actuales) with
        {
            Margen = pedidos.Margen, MargenUnico = pedidos.MargenUnico, TramosPorTrabajo = pedidos.TramosPorTrabajo, TramosPorHoja = pedidos.TramosPorHoja,
            MargenPapel = pedidos.MargenPapel, Curvas = pedidos.Curvas ?? throw new ArgumentException("Faltan las curvas del margen."),
        };
        return MargenesDeImpresora.De(Aplicar(actuales, dto));
    }

    /// <summary>Lo más que se puede escribir en las caras de la curva por caras.</summary>
    public const int CarasMaximas = 1_000_000;

    /// <summary>
    /// Las curvas como las pide la Configuración (o el probador), revisadas: cada una de 2 a <see cref="MarginCurves.MaxPoints"/> puntos, en
    /// orden de x, sin repetir; la tinta de más de 0 a 400 %, con su margen de 0 a 1,000 %; las caras, enteras de 1 a 1,000,000, con su factor
    /// de 0 a 200 %. Si algo no sirve, lo dice (<see cref="ArgumentException"/>).
    /// </summary>
    public static MarginCurves Curvas(CurvasMargenDto pedidas)
    {
        var porTinta = (pedidas.PorTinta ?? []).OrderBy(p => p.X).ToList();
        var porCaras = (pedidas.PorCaras ?? []).OrderBy(p => p.X).ToList();
        Puntos(porTinta, "por la tinta de cada cara");
        Puntos(porCaras, "por las caras del trabajo");
        foreach (var p in porTinta)
        {
            if (p.X <= 0 || p.X > MarginCurves.MaxCoverage)
                throw new ArgumentException($"La tinta de un punto de la curva por tinta va de más de 0 a {Porcentaje(MarginCurves.MaxCoverage)} "
                    + "(una carta de texto ≈ 5 %, con fotos ≈ 40 %, toda llena ≈ 100 % o más).");
            Revisar(p.Y, 0, MargenMaximo, $"El margen del punto de {Porcentaje(p.X)} de tinta", "%");
        }
        foreach (var p in porCaras)
        {
            if (p.X < 1 || p.X > CarasMaximas || p.X != Math.Floor(p.X))
                throw new ArgumentException($"Las caras de un punto de la curva por caras son enteras, de 1 a {Entero(CarasMaximas)}.");
            Revisar(p.Y, 0, MarginCurves.MaxFactor, $"El factor del punto de {Entero((int)p.X)} caras", "%");
        }
        return new MarginCurves
        {
            ByCoverage = porTinta.Select(p => new CurvePoint(p.X, p.Y)).ToList(),
            BySides = porCaras.Select(p => new CurvePoint(p.X, p.Y)).ToList(),
        };
    }

    private static void Puntos(IReadOnlyList<PuntoCurvaDto> puntos, string de)
    {
        if (puntos.Count < 2)
            throw new ArgumentException($"La curva {de} necesita al menos 2 puntos.");
        if (puntos.Count > MarginCurves.MaxPoints)
            throw new ArgumentException($"La curva {de} es de hasta {MarginCurves.MaxPoints} puntos.");
        if (puntos.GroupBy(p => p.X).FirstOrDefault(g => g.Count() > 1) is { } repetido)
            throw new ArgumentException($"Dos puntos de la curva {de} están en {repetido.Key.ToString("0.##", Mexico)}: cada uno va en otro lugar.");
    }

    /// <summary>Los precios que faltan (en cero): con ellos en cero el precio sale de menos («Papel Carta normal»).</summary>
    public static IReadOnlyList<string> Faltan(CostSettings c) => c.MissingPrices().ToList();

    /// <summary>
    /// Una copia de <paramref name="actual"/> con lo que dice <paramref name="pedidos"/>; lo demás, como estaba. Si algo no sirve, lo
    /// dice (<see cref="ArgumentException"/>) sin cambiar nada.
    /// </summary>
    public static CostSettings Aplicar(CostSettings actual, CostosImpresionDto pedidos)
    {
        var c = actual.Clone();
        foreach (var tinta in Tintas)
        {
            var nombre = Calibrator.Name(tinta);
            var pedido = (pedidos.Cartuchos ?? []).FirstOrDefault(x => string.Equals(x.Tinta, nombre, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Falta el cartucho {nombre.ToLowerInvariant()}.");
            var cartucho = $"cartucho {nombre.ToLowerInvariant()}";
            Revisar(pedido.Precio, 0, PrecioMaximo, $"El precio del {cartucho}");
            if (pedido.Rendimiento is < 1 or > RendimientoMaximo)
                throw new ArgumentException($"Lo que rinde el {cartucho} va de 1 a {Entero(RendimientoMaximo)} páginas (lo dice su caja).");
            (c.Ink(tinta).Price, c.Ink(tinta).IsoYield) = (pedido.Precio, pedido.Rendimiento);
        }
        foreach (var pedido in pedidos.Papeles ?? [])
        {
            var papel = c.Papers.FirstOrDefault(p => p.Size.ToString() == pedido.Tamano && p.Group == pedido.Grupo)
                ?? throw new ArgumentException($"No hay papel {pedido.Tamano} «{pedido.Grupo}» en los costos.");
            Revisar(pedido.PrecioPaquete, 0, PrecioMaximo, $"El precio del paquete de papel {Papel(papel)}");
            if (pedido.HojasPaquete is < 1 or > HojasMaximas)
                throw new ArgumentException($"El paquete de papel {Papel(papel)} trae de 1 a {Entero(HojasMaximas)} hojas.");
            (papel.PackPrice, papel.PackSheets) = (pedido.PrecioPaquete, pedido.HojasPaquete);
        }
        Revisar(pedidos.CajaMantenimiento, 0, PrecioMaximo, "El precio de la caja de mantenimiento");
        c.Maintenance.BoxPrice = pedidos.CajaMantenimiento;

        if (!Enum.IsDefined(pedidos.Margen))
            throw new ArgumentException("El margen es uno para todo, por el costo del trabajo, por el costo de cada hoja o por las curvas.");
        c.Scaling = pedidos.Margen switch
        {
            MargenDeImpresiones.PorCostoDelTrabajo => ScalingMode.ByJobCost,
            MargenDeImpresiones.PorCostoDeLaHoja => ScalingMode.BySheetCost,
            MargenDeImpresiones.PorCurvas => ScalingMode.ByCurves,
            _ => ScalingMode.None,
        };
        // Una caja de antes no las manda: se quedan como están.
        if (pedidos.Curvas is { } curvas)
            c.Curves = Curvas(curvas);
        Revisar(pedidos.MargenUnico, 0, MargenMaximo, "El margen", "%");
        c.MarginPercent = pedidos.MargenUnico;
        if (pedidos.MargenPapel is { } margenPapel)
            Revisar(margenPapel, 0, MargenMaximo, "El margen del papel", "%");
        c.PaperMarginPercent = pedidos.MargenPapel;
        c.JobCostTiers = Tramos(pedidos.TramosPorTrabajo, "del costo del trabajo", pedidos.Margen == MargenDeImpresiones.PorCostoDelTrabajo);
        c.SheetCostTiers = Tramos(pedidos.TramosPorHoja, "del costo de cada hoja", pedidos.Margen == MargenDeImpresiones.PorCostoDeLaHoja);

        Revisar(pedidos.CobroMinimo, 0, PrecioMaximo, "El cobro mínimo");
        c.MinimumPrice = pedidos.CobroMinimo;
        c.MinimumApplies = pedidos.MinimoATodos ? MinimumScope.AllJobs : MinimumScope.SinglePage;
        Revisar(pedidos.RedondeoA, 0, RedondeoMaximo, "El redondeo");
        c.RoundTo = pedidos.RedondeoA;
        return c;
    }

    /// <summary>
    /// Qué cambió, para la bitácora, sin los montos (revisión del 2026-10-31: la bitácora la lee quien tiene «Consultar la bitácora»
    /// aunque no vea los costos): «el precio del cartucho negro», «las hojas del paquete de papel Carta normal». Lo de antes, con sus
    /// montos, se queda en el historial de los costos. Vacío si nada cambió.
    /// </summary>
    public static List<string> Cambios(CostSettings antes, CostSettings despues)
    {
        var cambios = new List<string>();
        void Si<T>(string que, T de, T a)
        {
            if (!EqualityComparer<T>.Default.Equals(de, a))
                cambios.Add(que);
        }
        foreach (var tinta in Tintas)
        {
            var nombre = Calibrator.Name(tinta).ToLowerInvariant();
            Si($"el precio del cartucho {nombre}", antes.Ink(tinta).Price, despues.Ink(tinta).Price);
            Si($"lo que rinde el cartucho {nombre}", antes.Ink(tinta).IsoYield, despues.Ink(tinta).IsoYield);
        }
        foreach (var papel in despues.Papers)
        {
            if (antes.Papers.FirstOrDefault(p => p.Size == papel.Size && p.Group == papel.Group) is not { } previo)
                continue;
            Si($"el precio del paquete de papel {Papel(papel)}", previo.PackPrice, papel.PackPrice);
            Si($"las hojas del paquete de papel {Papel(papel)}", previo.PackSheets, papel.PackSheets);
        }
        Si("el precio de la caja de mantenimiento", antes.Maintenance.BoxPrice, despues.Maintenance.BoxPrice);
        Si("cómo se calcula el margen", antes.Scaling, despues.Scaling);
        Si("el margen para todo", antes.MarginPercent, despues.MarginPercent);
        Si("los tramos del margen por el costo del trabajo", TextoTramos(antes.JobCostTiers), TextoTramos(despues.JobCostTiers));
        Si("los tramos del margen por el costo de cada hoja", TextoTramos(antes.SheetCostTiers), TextoTramos(despues.SheetCostTiers));
        Si("el margen del papel", antes.PaperMarginPercent, despues.PaperMarginPercent);
        Si("la curva del margen por la tinta de cada cara", TextoCurva(antes.Curves.ByCoverage), TextoCurva(despues.Curves.ByCoverage));
        Si("la curva del margen por las caras del trabajo", TextoCurva(antes.Curves.BySides), TextoCurva(despues.Curves.BySides));
        Si("el cobro mínimo", antes.MinimumPrice, despues.MinimumPrice);
        Si("a qué se cobra el mínimo", antes.MinimumApplies, despues.MinimumApplies);
        Si("el redondeo", antes.RoundTo, despues.RoundTo);
        return cambios;
    }

    /// <summary>Los cambios en un renglón de la bitácora: «el precio del cartucho negro y lo que rinde el cartucho negro».</summary>
    public static string Lista(IReadOnlyList<string> cambios) => cambios.Count switch
    {
        0 => "",
        1 => cambios[0],
        _ => $"{string.Join(", ", cambios.Take(cambios.Count - 1))} y {cambios[^1]}",
    };

    /// <summary>«Carta normal», «Carta Fotográfico brillante».</summary>
    public static string Papel(PaperPrice p) => p.Group == "Normal" ? $"{p.Size} normal" : $"{p.Size} {p.Group}";

    private static IReadOnlyList<TramoMargenDto> Tramos(IEnumerable<MarginTier>? tramos) =>
        (tramos ?? []).OrderBy(t => t.From).Select(t => new TramoMargenDto(t.From, t.Margin)).ToList();

    private static List<MarginTier> Tramos(IReadOnlyList<TramoMargenDto>? pedidos, string de, bool seUsa)
    {
        var tramos = (pedidos ?? []).OrderBy(t => t.Desde).ToList();
        if (seUsa && tramos.Count == 0)
            throw new ArgumentException($"Con el margen por tramos {de}, escribe al menos un tramo.");
        foreach (var tramo in tramos)
        {
            Revisar(tramo.Desde, 0, PrecioMaximo, $"El «desde» de un tramo {de}");
            Revisar(tramo.Margen, 0, MargenMaximo, $"El margen de un tramo {de}", "%");
        }
        if (tramos.GroupBy(t => t.Desde).FirstOrDefault(g => g.Count() > 1) is { } repetido)
            throw new ArgumentException($"Dos tramos {de} empiezan en {Pesos(repetido.Key)}: cada uno empieza en otro costo.");
        return tramos.Select(t => new MarginTier { From = t.Desde, Margin = t.Margen }).ToList();
    }

    private static string TextoCurva(IEnumerable<CurvePoint> puntos) =>
        string.Join(", ", puntos.OrderBy(p => p.X).Select(p => $"{p.X.ToString("0.####", Mexico)}→{p.Y.ToString("0.####", Mexico)}"));

    private static string TextoTramos(IEnumerable<MarginTier> tramos) =>
        string.Join(", ", tramos.OrderBy(t => t.From).Select(t => $"desde {Pesos(t.From)} {Porcentaje(t.Margin)}"));

    private static void Revisar(decimal valor, decimal minimo, decimal maximo, string que, string? unidad = null)
    {
        if (valor < minimo || valor > maximo)
            throw new ArgumentException(unidad == "%"
                ? $"{que} va de {minimo.ToString("0.##", Mexico)} a {maximo.ToString("N0", Mexico)} %."
                : $"{que} va de {Pesos(minimo)} a {Pesos(maximo)}.");
    }

    private static string Pesos(decimal valor) => valor.ToString("C2", Mexico);

    private static string Entero(int valor) => valor.ToString("N0", Mexico);

    private static string Porcentaje(decimal valor) => $"{valor.ToString("0.##", Mexico)} %";
}
