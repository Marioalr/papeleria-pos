using System.Globalization;

namespace PapeleriaPos.Nucleo.Ventas;

/// <summary>
/// El porcentaje junto a un descuento en pesos (dueño, 2026-11-22): «−$5.00 (10 %)». Con un decimal y sin «.0»: «10 %», «8.3 %».
/// El de un renglón es sobre lo que costaba sin descuento; el de todo el ticket, sobre el total del ticket sin descuentos.
/// </summary>
public static class PorcentajeDelDescuento
{
    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>«10 %» de <paramref name="descuentoCentavos"/> sobre <paramref name="sinDescuentoCentavos"/>; vacío si no hay sobre qué.</summary>
    public static string Texto(long descuentoCentavos, long sinDescuentoCentavos) =>
        sinDescuentoCentavos <= 0 || descuentoCentavos <= 0 ? "" : Texto(descuentoCentavos * 100m / sinDescuentoCentavos);

    /// <summary>«12.5 %», «33.3 %», «10 %»: un decimal, redondeado (los medios hacia arriba), sin «.0».</summary>
    public static string Texto(decimal porcentaje) =>
        $"{Math.Round(porcentaje, 1, MidpointRounding.AwayFromZero).ToString("0.#", Mexico)} %";

    /// <summary>El de un renglón: el que se escribió en porcentaje (redondeado a un decimal) o el que sale del monto.</summary>
    public static string DeLinea(VentaLinea linea) =>
        linea.DescuentoCentesimas is { } c && linea.DescuentoCentavos > 0 ? Texto(c / 100m)
        : Texto(linea.DescuentoCentavos, linea.ImporteSinDescuentoCentavos);

    /// <summary>El de todo el ticket: lo descontado sobre el total sin descuentos (con las impresiones, que no llevan).</summary>
    public static string DelTicket(Venta venta) => Texto(venta.DescuentoCentavos, venta.TotalCentavos + venta.DescuentoCentavos);
}
