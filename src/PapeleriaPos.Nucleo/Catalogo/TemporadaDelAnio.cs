namespace PapeleriaPos.Nucleo.Catalogo;

// Empezar y terminar la temporada (entrega 13, parte 3; docs/propuesta-interfaz.md, «Lo que se señala», puntos 3 y 4): una
// vez al año, antes de sacar la mercancía, se confirman sus precios con la lista del año pasado; al terminar, lo que sobró se
// cuenta y queda el resumen de qué se vendió y qué no, para comprar mejor.

/// <summary>
/// Las fechas de una temporada en un año: del día en que empieza al día en que termina (los dos incluidos). Navidad, del 20
/// de noviembre al 1 de enero, empieza un año y termina el siguiente.
/// </summary>
public readonly record struct PeriodoDeTemporada(DateOnly Inicio, DateOnly Fin)
{
    /// <summary>Si ese día cae en el periodo (con los dos extremos).</summary>
    public bool Contiene(DateOnly dia) => dia >= Inicio && dia <= Fin;

    /// <summary>«del 20/11/2026 al 1/1/2027».</summary>
    public override string ToString() =>
        $"del {Inicio.ToString("d/M/yyyy", System.Globalization.CultureInfo.InvariantCulture)} al {Fin.ToString("d/M/yyyy", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// El periodo que empieza ese año. Un 29 de febrero, en un año sin él, es el 28 (el último de febrero): así la temporada
    /// no se salta ese año.
    /// </summary>
    public static PeriodoDeTemporada DelAnio(DiaDelAnio desde, DiaDelAnio hasta, int anio)
    {
        var cruzaElAnio = hasta.Mes * 100 + hasta.Dia < desde.Mes * 100 + desde.Dia;
        return new PeriodoDeTemporada(Fecha(anio, desde), Fecha(cruzaElAnio ? anio + 1 : anio, hasta));
    }

    /// <summary>
    /// La que se prepara al empezarla: la que corre ese día, o si no corre, la siguiente que va a empezar. Navidad el 17 de
    /// octubre de 2026: del 20/11/2026 al 1/1/2027; el 30 de diciembre de 2026, esa misma (todavía corre).
    /// </summary>
    public static PeriodoDeTemporada QueSigue(DiaDelAnio desde, DiaDelAnio hasta, DateOnly hoy)
    {
        foreach (var anio in new[] { hoy.Year - 1, hoy.Year, hoy.Year + 1 })
        {
            var periodo = DelAnio(desde, hasta, anio);
            if (periodo.Contiene(hoy) || periodo.Inicio > hoy)
                return periodo;
        }
        return DelAnio(desde, hasta, hoy.Year + 1);
    }

    /// <summary>
    /// La última que ya empezó ese día (corra todavía o ya haya terminado): la que se termina. Todos Santos el 10 de noviembre
    /// de 2026: del 16/9/2026 al 5/11/2026.
    /// </summary>
    public static PeriodoDeTemporada UltimaQueEmpezo(DiaDelAnio desde, DiaDelAnio hasta, DateOnly hoy)
    {
        var deEsteAnio = DelAnio(desde, hasta, hoy.Year);
        return deEsteAnio.Inicio <= hoy ? deEsteAnio : DelAnio(desde, hasta, hoy.Year - 1);
    }

    /// <summary>La del año anterior: con la que se arma la lista al empezar.</summary>
    public PeriodoDeTemporada AnioAnterior(DiaDelAnio desde, DiaDelAnio hasta) => DelAnio(desde, hasta, Inicio.Year - 1);

    /// <summary>Desde cuándo y hasta cuándo, en UTC (como se guarda todo): de la medianoche del primer día a la del día siguiente al último.</summary>
    public (DateTime Desde, DateTime Hasta) EnUtc() =>
        (HoraMexico.AUtc(Inicio.ToDateTime(TimeOnly.MinValue)), HoraMexico.AUtc(Fin.AddDays(1).ToDateTime(TimeOnly.MinValue)));

    private static DateOnly Fecha(int anio, DiaDelAnio dia) => new(anio, dia.Mes, Math.Min(dia.Dia, DateTime.DaysInMonth(anio, dia.Mes)));
}

/// <summary>
/// Una temporada en un año (Navidad 2026): cuándo se confirmaron sus precios y cuándo se terminó, con el resumen que quedó.
/// Se crea al confirmar los precios o al terminarla. Solo es del POS: en el piloto con eleventa también se lleva aquí.
/// </summary>
public class TemporadaDelAnio
{
    public Guid Id { get; set; } = Ids.Nuevo();

    /// <summary>La temporada (un <see cref="Exhibidor"/> de tipo temporada).</summary>
    public Guid ExhibidorId { get; set; }

    public Guid SucursalId { get; set; }

    /// <summary>El primer día de esa vez (con él se distingue la de cada año) y el último.</summary>
    public DateOnly Inicio { get; set; }

    public DateOnly Fin { get; set; }

    /// <summary>Cuándo y quién confirmó sus precios («Empezar la temporada»); vacío si no se han confirmado.</summary>
    public DateTime? PreciosConfirmadosUtc { get; set; }

    public Guid? PreciosConfirmadosPorId { get; set; }

    /// <summary>Cuándo y quién la terminó; vacío mientras no se termina.</summary>
    public DateTime? TerminadaUtc { get; set; }

    public Guid? TerminadaPorId { get; set; }

    /// <summary>Si al terminarla se contó lo que sobró (fuera del piloto con eleventa).</summary>
    public bool Contada { get; set; }

    /// <summary>El resumen que quedó al terminarla, un renglón por producto, en el orden de la mesa.</summary>
    public List<RenglonTemporadaDelAnio> Renglones { get; set; } = [];
}

/// <summary>
/// Lo que se contó de un producto al terminar la temporada, con lo que decía el sistema en ese momento, como las capturas del
/// conteo completo: al terminarla entra al kardex la diferencia (contado − esperado), así lo que se vendió después de contarlo
/// sigue contando y no regresa a la existencia. Uno por producto en cada temporada del año: contarlo otra vez lo reemplaza.
/// </summary>
public class ContadoDeTemporada
{
    public Guid Id { get; set; } = Ids.Nuevo();

    /// <summary>La temporada de ese año (<see cref="TemporadaDelAnio"/>).</summary>
    public Guid TemporadaId { get; set; }

    public Guid VarianteId { get; set; }
    public Cantidad Contado { get; set; }

    /// <summary>La existencia que decía el sistema cuando se contó.</summary>
    public Cantidad Esperado { get; set; }

    public DateTime ContadoUtc { get; set; }
    public Guid UsuarioId { get; set; }

    /// <summary>Lo que entra al kardex al terminarla: lo que faltó (negativo) o lo que sobró.</summary>
    public Cantidad Diferencia => Contado - Esperado;
}

/// <summary>
/// Un producto en el resumen de una temporada terminada: lo que había (al empezar, más lo que llegó en la temporada), lo que
/// se vendió y lo que quedó, en unidad base.
/// </summary>
public class RenglonTemporadaDelAnio
{
    public Guid Id { get; set; } = Ids.Nuevo();
    public Guid TemporadaId { get; set; }
    public Guid VarianteId { get; set; }

    /// <summary>En qué lugar salía en la lista (el orden de la mesa al terminarla).</summary>
    public int Posicion { get; set; }

    public Cantidad Inicial { get; set; }
    public Cantidad Vendido { get; set; }
    public Cantidad Quedo { get; set; }
}

/// <summary>
/// Las reglas de los precios al empezar la temporada (docs/propuesta-interfaz.md, «Lo que se señala», punto 3): cada producto
/// con el precio que tuvo el año pasado, el de hoy, el costo de hoy y un sugerido; se confirman una vez. Las mismas que al
/// cambiar los precios de varios: el mayoreo nunca queda igual o más caro que el menudeo, y el costo y el margen los ve solo
/// quien tiene «Ver costos, utilidad y margen».
/// </summary>
public static class PreciosDeTemporada
{
    /// <summary>El precio más alto que se escribe: $100,000.00. Más es un error de dedo.</summary>
    public const long MaximoCentavos = 10_000_000;

    /// <summary>
    /// Lo más bajo que puede quedar el menudeo para que su mayoreo siga más barato: cada escala por cantidad, abajo del menudeo
    /// (nunca igual); cada paquete con precio propio, por pieza, igual o más barato que el menudeo, y más barato si hoy ya lo
    /// es (si tenía descuento, lo sigue teniendo). Cero si no tiene mayoreo. El mayoreo no se toca al confirmar: si el precio
    /// que se escribe queda abajo de esto, ese precio no se confirma.
    /// </summary>
    /// <param name="menudeoCentavos">El menudeo de hoy: con él se sabe si el paquete tenía descuento.</param>
    /// <param name="escalas">Las de la unidad base con cantidad mínima arriba del menudeo, sus precios.</param>
    /// <param name="paquetes">Los precios propios de cada paquete (el de menos paquetes), con las piezas que trae en milésimas.</param>
    public static long Piso(long menudeoCentavos, IEnumerable<long> escalas, IEnumerable<(long FactorMilesimas, long PrecioCentavos)> paquetes)
    {
        var piso = escalas.Where(p => p > 0).Select(p => p + 1).DefaultIfEmpty(0).Max();
        foreach (var (factor, precio) in paquetes)
        {
            if (factor <= 0 || precio <= 0)
                continue;
            // El paquete cuesta lo que sus piezas a menudeo como mucho: precio ≤ piezas × menudeo.
            var exacto = (Int128)precio * Cantidad.Escala;
            var conDescuento = menudeoCentavos > 0 && precio < new Cantidad(factor).Importe(menudeoCentavos);
            var minimo = conDescuento ? (long)(exacto / factor) + 1 : (long)((exacto + factor - 1) / factor);
            piso = Math.Max(piso, minimo);
        }
        return piso;
    }

    /// <summary>
    /// El sugerido: el más alto entre el de hoy y el del año pasado (lo que se rebajó al final de la temporada pasada no se
    /// regala otra vez), subido al que deja el margen mínimo con el costo de hoy si deja menos (redondeado a los 50 centavos
    /// de arriba, como el sugerido de las compras). Nunca baja un precio. Vacío sin costo: no hay con qué medir el margen.
    /// </summary>
    public static long? Sugerido(long? hoyCentavos, long? anteriorCentavos, decimal? costoCentavos, int margenMinimoCentesimas)
    {
        if (costoCentavos is not { } costo || costo <= 0)
            return null;
        var elMasAlto = Math.Max(hoyCentavos ?? 0, anteriorCentavos ?? 0);
        return Math.Max(elMasAlto, Margen.PrecioSugerido(costo, margenMinimoCentesimas));
    }
}
