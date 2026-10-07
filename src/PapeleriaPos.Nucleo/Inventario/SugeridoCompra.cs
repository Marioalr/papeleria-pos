namespace PapeleriaPos.Nucleo.Inventario;

/// <summary>
/// El sugerido de compra (docs/fase-2-inventario.md, entrega 4, parte 3): con lo que se vende al día (las ventas del
/// POS y el historial de eleventa) y la temporada (lo que pasó en estas mismas semanas el año pasado, como el regreso a
/// clases), cuánto debe haber de cada producto y cuánto pedir.
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
    }

    /// <param name="Ventas">Lo vendido por día (local), en unidad base: el último año y algo más.</param>
    /// <param name="EnCamino">Lo que ya se pidió en órdenes abiertas, en unidad base.</param>
    /// <param name="FactorCompra">Cuántas unidades trae lo que se compra (la caja de 12): se pide en enteros de eso.</param>
    /// <param name="Fracciones">Si se vende por metro o kilo (la cantidad no se redondea a piezas).</param>
    public sealed record Producto(Guid VarianteId, IReadOnlyDictionary<DateOnly, decimal> Ventas, decimal Existencia, decimal EnCamino, decimal FactorCompra, bool Fracciones);

    /// <param name="VentaDiaria">Ya con la temporada.</param>
    /// <param name="Temporada">Cuánto cambia la venta en estas semanas según el año pasado (1 = igual).</param>
    /// <param name="Minimo">Con esto o menos ya urge pedir.</param>
    /// <param name="Maximo">Hasta donde se pide.</param>
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
        var falta = maximo - disponible;
        var factor = p.FactorCompra > 0 ? p.FactorCompra : 1;
        // Debajo del mínimo se completa el paquete; arriba, solo se piden paquetes enteros que de verdad hagan falta.
        var paquetes = falta <= 0 || diaria == 0 ? 0 : (int)(urgente ? Math.Ceiling(falta / factor) : Math.Floor(falta / factor));
        return new Resultado(p.VarianteId, Math.Round(diaria, 3), Math.Round(temporada, 2), minimo, maximo, disponible, paquetes, urgente);
    }

    private static decimal Redondear(decimal cantidad, bool fracciones) => fracciones ? Math.Round(cantidad, 1) : Math.Ceiling(cantidad);
}
