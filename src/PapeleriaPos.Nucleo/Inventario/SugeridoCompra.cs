namespace PapeleriaPos.Nucleo.Inventario;

/// <summary>
/// El sugerido de compra (docs/fase-2-inventario.md, entrega 4, parte 3): con lo que se vende al día (las ventas del
/// POS y el historial de eleventa) y la temporada (lo que pasó en estas mismas semanas el año pasado, como el regreso a
/// clases), cuánto debe haber de cada producto y cuánto pedir.
/// <para>
/// Con el mínimo y el máximo propios de su ficha (2026-11-07, el dueño: «para que la lista de compra coincida» con «Bajos en
/// inventario»; decidido, por revisar): con su mínimo propio, urge cuando lo disponible está en él o debajo, aunque no se haya
/// vendido; y se pide por lo menos hasta su máximo propio (sin él, hasta su mínimo), con el cálculo de la temporada encima: la
/// meta es la mayor entre la propia y la calculada. Sin mínimo ni máximo propios, como siempre.
/// </para>
/// </summary>
public static class SugeridoCompra
{
    /// <summary>Lo reciente: lo que se vendió en estos días es la venta diaria de base.</summary>
    public const int DiasRecientes = 90;

    /// <summary>Un producto nuevo no se mide en menos de esto (dos semanas de ventas no dicen mucho, pero algo).</summary>
    public const int DiasMinimos = 14;

    /// <summary>La temporada no puede multiplicar ni dividir la venta más que esto: un año raro no debe disparar el pedido.</summary>
    public const decimal TemporadaMaxima = 4m, TemporadaMinima = 0.25m;

    /// <param name="CoberturaDias">Cada cuánto se compra: lo que tiene que alcanzar un pedido.</param>
    /// <param name="ColchonDias">Lo que se guarda de más por si el proveedor tarda o se vende más: es el mínimo.</param>
    public sealed record Parametros(int CoberturaDias, int ColchonDias)
    {
        public static readonly Parametros PorOmision = new(14, 7);

        public int HorizonteDias => CoberturaDias + ColchonDias;

        /// <summary>Con «Administrar mínimos»: la cobertura de un producto es cada cuánto se surte, si se sabe; si no, la general.</summary>
        public Parametros ConCobertura(int? seSurteCadaDias) => seSurteCadaDias is { } dias ? this with { CoberturaDias = dias } : this;
    }

    // ── «Administrar mínimos» (dueño, 2026-11-16) ──

    /// <summary>Las entradas que se miran para saber cada cuánto se surte un producto: las del último año.</summary>
    public const int DiasDeEntradas = 365;

    /// <summary>Con menos entradas (días distintos) no se sabe cada cuánto se surte: se usa la cobertura general.</summary>
    public const int EntradasMinimas = 3;

    /// <summary>Cada cuánto se surte no baja de una semana ni pasa de dos meses: una compra rara no debe mover tanto el pedido.</summary>
    public const int SurtidoMinimoDias = 7, SurtidoMaximoDias = 60;

    /// <summary>
    /// Cada cuántos días se surte un producto, según cómo se ha ido comprando: la mediana de los días entre sus entradas (las
    /// compras cerradas y «Agregar inventario») del último año, acotada a <see cref="SurtidoMinimoDias"/>–<see cref="SurtidoMaximoDias"/>.
    /// Varias entradas el mismo día cuentan como una. Con menos de <see cref="EntradasMinimas"/>, vacío: no se sabe.
    /// </summary>
    /// <param name="entradas">El día local de cada entrada.</param>
    public static int? CadaCuantoSeSurte(IEnumerable<DateOnly> entradas, DateOnly hoy)
    {
        var desde = hoy.AddDays(-DiasDeEntradas);
        var dias = entradas.Where(d => d >= desde && d <= hoy).Distinct().Order().ToList();
        if (dias.Count < EntradasMinimas)
            return null;
        var entre = dias.Zip(dias.Skip(1), (a, b) => b.DayNumber - a.DayNumber).Order().ToList();
        var mitad = entre.Count / 2;
        var mediana = entre.Count % 2 == 1 ? entre[mitad] : (int)Math.Round((entre[mitad - 1] + entre[mitad]) / 2m, MidpointRounding.AwayFromZero);
        return Math.Clamp(mediana, SurtidoMinimoDias, SurtidoMaximoDias);
    }

    /// <param name="Ventas">Lo vendido por día (local), en unidad base: el último año y algo más.</param>
    /// <param name="EnCamino">Lo que ya se pidió en órdenes abiertas, en unidad base.</param>
    /// <param name="FactorCompra">Cuántas unidades trae lo que se compra (la caja de 12): se pide en enteros de eso.</param>
    /// <param name="Fracciones">Si se vende por metro o kilo (la cantidad no se redondea a piezas).</param>
    /// <param name="MinimoPropio">El de su ficha, en unidad base: con eso o menos urge.</param>
    /// <param name="MaximoPropio">El de su ficha: se pide por lo menos hasta él.</param>
    public sealed record Producto(Guid VarianteId, IReadOnlyDictionary<DateOnly, decimal> Ventas, decimal Existencia, decimal EnCamino, decimal FactorCompra, bool Fracciones,
        decimal? MinimoPropio = null, decimal? MaximoPropio = null);

    /// <param name="VentaDiaria">Ya con la temporada.</param>
    /// <param name="Temporada">Cuánto cambia la venta en estas semanas según el año pasado (1 = igual).</param>
    /// <param name="Minimo">Con esto o menos ya urge pedir: el propio, si tiene.</param>
    /// <param name="Maximo">Hasta donde se pide: el mayor entre el propio (o, sin él, el mínimo propio) y el calculado.</param>
    /// <param name="Paquetes">Cuántos de la presentación de compra pedir (cero: no hace falta).</param>
    public sealed record Resultado(Guid VarianteId, decimal VentaDiaria, decimal Temporada, decimal Minimo, decimal Maximo, decimal Disponible, int Paquetes, bool Urgente)
    {
        public decimal Sugerido(decimal factor) => Paquetes * factor;
    }

    public static Resultado Calcular(Producto p, DateOnly hoy, Parametros parametros)
    {
        decimal Suma(DateOnly desde, DateOnly hasta) => p.Ventas.Where(v => v.Key >= desde && v.Key < hasta).Sum(v => v.Value);

        // Lo reciente; un producto que empezó a venderse hace poco se mide desde su primera venta.
        var desde = hoy.AddDays(-DiasRecientes);
        var primera = p.Ventas.Where(v => v.Value > 0).Select(v => v.Key).DefaultIfEmpty(hoy).Min();
        var dias = Math.Clamp(hoy.DayNumber - Math.Max(primera.DayNumber, desde.DayNumber), DiasMinimos, DiasRecientes);
        var diaria = Suma(hoy.AddDays(-dias), hoy) / dias;

        // La temporada: cómo se vendieron las semanas que vienen el año pasado contra los 90 días que las precedieron.
        var haceUnAno = hoy.AddYears(-1);
        var antes = Suma(haceUnAno.AddDays(-DiasRecientes), haceUnAno) / DiasRecientes;
        var proximas = Suma(haceUnAno, haceUnAno.AddDays(parametros.HorizonteDias)) / parametros.HorizonteDias;
        var temporada = antes > 0 && proximas > 0 ? Math.Clamp(proximas / antes, TemporadaMinima, TemporadaMaxima) : 1m;
        // Si este año no se ha vendido pero el año pasado en estas semanas sí, se toma lo del año pasado.
        if (diaria == 0 && proximas > 0)
            (diaria, temporada) = (proximas, 1m);
        else
            diaria *= temporada;

        var minimo = Redondear(diaria * parametros.ColchonDias, p.Fracciones);
        var maximo = Redondear(diaria * parametros.HorizonteDias, p.Fracciones);
        var disponible = Math.Max(0, p.Existencia) + p.EnCamino;
        var urgente = diaria > 0 && disponible <= minimo;
        // Los de su ficha: el mínimo propio manda en cuándo urge, y la meta es la mayor entre la propia y la calculada.
        var propios = p.MinimoPropio is not null || p.MaximoPropio is not null;
        if (p.MinimoPropio is { } minimoPropio)
            (minimo, urgente) = (minimoPropio, disponible <= minimoPropio);
        if ((p.MaximoPropio ?? p.MinimoPropio) is { } meta)
            maximo = Math.Max(maximo, meta);
        var falta = maximo - disponible;
        var factor = p.FactorCompra > 0 ? p.FactorCompra : 1;
        // Debajo del mínimo se completa el paquete; arriba, solo se piden paquetes enteros que de verdad hagan falta. Sin ventas,
        // solo con los propios.
        var paquetes = falta <= 0 || (diaria == 0 && !propios) ? 0 : (int)(urgente ? Math.Ceiling(falta / factor) : Math.Floor(falta / factor));
        return new Resultado(p.VarianteId, Math.Round(diaria, 3), Math.Round(temporada, 2), minimo, maximo, disponible, paquetes, urgente);
    }

    private static decimal Redondear(decimal cantidad, bool fracciones) => fracciones ? Math.Round(cantidad, 1) : Math.Ceiling(cantidad);
}
