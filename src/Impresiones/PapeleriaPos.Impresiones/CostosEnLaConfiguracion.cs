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
        c.Scaling switch
        {
            ScalingMode.ByJobCost => MargenDeImpresiones.PorCostoDelTrabajo,
            ScalingMode.BySheetCost => MargenDeImpresiones.PorCostoDeLaHoja,
            _ => MargenDeImpresiones.Unico,
        },
        c.MarginPercent, Tramos(c.JobCostTiers), Tramos(c.SheetCostTiers), c.MinimumPrice, c.MinimumApplies == MinimumScope.AllJobs, c.RoundTo);

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
            throw new ArgumentException("El margen es uno para todo, por el costo del trabajo o por el costo de cada hoja.");
        c.Scaling = pedidos.Margen switch
        {
            MargenDeImpresiones.PorCostoDelTrabajo => ScalingMode.ByJobCost,
            MargenDeImpresiones.PorCostoDeLaHoja => ScalingMode.BySheetCost,
            _ => ScalingMode.None,
        };
        Revisar(pedidos.MargenUnico, 0, MargenMaximo, "El margen", "%");
        c.MarginPercent = pedidos.MargenUnico;
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
    /// Lo que cambió, para la bitácora: «el cartucho negro: $1,200.00 → $1,150.00», «el margen: por el costo del trabajo → uno para
    /// todo». Vacío si nada cambió.
    /// </summary>
    public static List<string> Cambios(CostSettings antes, CostSettings despues)
    {
        var cambios = new List<string>();
        void Si<T>(string que, T de, T a, Func<T, string> texto)
        {
            if (!EqualityComparer<T>.Default.Equals(de, a))
                cambios.Add($"{que}: {texto(de)} → {texto(a)}");
        }
        foreach (var tinta in Tintas)
        {
            var nombre = Calibrator.Name(tinta).ToLowerInvariant();
            Si($"el cartucho {nombre}", antes.Ink(tinta).Price, despues.Ink(tinta).Price, Pesos);
            Si($"lo que rinde el cartucho {nombre}", antes.Ink(tinta).IsoYield, despues.Ink(tinta).IsoYield, n => $"{Entero(n)} páginas");
        }
        foreach (var papel in despues.Papers)
        {
            if (antes.Papers.FirstOrDefault(p => p.Size == papel.Size && p.Group == papel.Group) is not { } previo)
                continue;
            Si($"el paquete de papel {Papel(papel)}", (previo.PackPrice, previo.PackSheets), (papel.PackPrice, papel.PackSheets),
                p => $"{Pesos(p.Item1)} por {Entero(p.Item2)} hojas");
        }
        Si("la caja de mantenimiento", antes.Maintenance.BoxPrice, despues.Maintenance.BoxPrice, Pesos);
        Si("el margen", antes.Scaling, despues.Scaling, Nombre);
        Si("el margen para todo", antes.MarginPercent, despues.MarginPercent, Porcentaje);
        Si("los tramos por el costo del trabajo", TextoTramos(antes.JobCostTiers), TextoTramos(despues.JobCostTiers), t => t);
        Si("los tramos por el costo de cada hoja", TextoTramos(antes.SheetCostTiers), TextoTramos(despues.SheetCostTiers), t => t);
        Si("el cobro mínimo", (antes.MinimumPrice, antes.MinimumApplies), (despues.MinimumPrice, despues.MinimumApplies),
            m => $"{Pesos(m.Item1)} {(m.Item2 == MinimumScope.AllJobs ? "a todo" : "a lo de una cara")}");
        Si("el redondeo", antes.RoundTo, despues.RoundTo, r => r <= 0 ? "al centavo" : $"a {Pesos(r)}");
        return cambios;
    }

    /// <summary>«Carta normal», «Carta Fotográfico brillante».</summary>
    public static string Papel(PaperPrice p) => p.Group == "Normal" ? $"{p.Size} normal" : $"{p.Size} {p.Group}";

    private static string Nombre(ScalingMode modo) => modo switch
    {
        ScalingMode.ByJobCost => "por el costo del trabajo",
        ScalingMode.BySheetCost => "por el costo de cada hoja",
        _ => "uno para todo",
    };

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
