using System.Globalization;
using PapeleriaPos.Herramientas.Diagnostico;
using PapeleriaPos.Nucleo;

namespace PapeleriaPos.Herramientas.Eleventa;

/// <summary>Una venta con F11 de un producto: cuántas piezas, cuándo (hora local) y cuánto ganó cada pieza.</summary>
public sealed record VentaConMayoreo(double Cantidad, DateTime VendidoEn, double Ganancia);

/// <param name="Paquete">Desde cuántas piezas aplica el mayoreo; vacío si no se pudo deducir (va a revisar).</param>
/// <param name="Motivo">Por qué no se dedujo.</param>
/// <param name="Ventas">Las ventas con F11 que se tomaron en cuenta.</param>
/// <param name="Cantidades">Las cantidades más vendidas con F11 y cuántas veces: "12 ×5 · 24 ×2".</param>
/// <param name="Sugerida">La cantidad más vendida, para proponerla al revisar.</param>
public sealed record ResultadoMayoreo(Cantidad? Paquete, string? Motivo, int Ventas, string Cantidades, Cantidad? Sugerida);

/// <summary>
/// De las ventas con F11 de eleventa sale el paquete (docs/fase-3-precios-clientes.md, entrega 1): en eleventa, al
/// vender un paquete se escribían sus piezas y se oprimía F11. El paquete es la cantidad que, con sus múltiplos (dos o
/// tres paquetes), junta más ventas, y desde ahí aplica la escala al precio de mayoreo. No cuentan las ventas del regreso
/// a clases (F11 servía para dejar al costo los productos estratégicos) ni las que no dejaron ganancia.
/// </summary>
public static class DeduccionMayoreo
{
    /// <summary>Un paquete que se vendió menos veces que esto (él solo, sin sus múltiplos) no basta para confiar en él.</summary>
    public const int VentasMinimas = 3;

    /// <summary>El paquete, con sus múltiplos, tiene que juntar al menos esta parte de las ventas con F11.</summary>
    public const decimal ParteMinima = 0.5m;

    public const string Deducido = "El paquete salió de sus ventas con F11";
    public const string SinVentas = "No se vendió con F11 (fuera del regreso a clases)";
    public const string PocasVentas = "Pocas ventas con F11 para saber el paquete";
    public const string SinPaquete = "Con F11 se vendieron cantidades distintas: ninguna domina";
    public const string Empate = "Dos cantidades empatan como la más vendida con F11";

    public static ResultadoMayoreo Deducir(IEnumerable<VentaConMayoreo> ventas)
    {
        var validas = ventas.Where(v => v.Ganancia > 0 && !Analisis.EsRegresoAClases(v.VendidoEn) && v.Cantidad > 0)
            .Select(v => Math.Round((decimal)v.Cantidad, 3)).ToList();
        var porCantidad = validas.GroupBy(c => c)
            .Select(g => (Cantidad: g.Key, Veces: g.Count()))
            .OrderByDescending(x => x.Veces).ThenBy(x => x.Cantidad)
            .ToList();
        var texto = string.Join(" · ", porCantidad.Take(4).Select(x => $"{x.Cantidad.ToString("0.###", CultureInfo.InvariantCulture)} ×{x.Veces}"));
        if (validas.Count == 0)
            return new ResultadoMayoreo(null, SinVentas, 0, "", null);

        // Cada cantidad entera que se vendió lo suficiente junta las ventas de sus múltiplos: con 12 ×3 y 24 ×4 el
        // paquete es 12 (las 7 ventas), no 24. Así un múltiplo que se vendió más no tapa al paquete de verdad.
        var candidatos = porCantidad.Where(x => x.Veces >= VentasMinimas && EsPaquete(x.Cantidad))
            .Select(x => (x.Cantidad, AFavor: validas.Count(c => c % x.Cantidad == 0)))
            .OrderByDescending(x => x.AFavor).ThenBy(x => x.Cantidad)
            .ToList();
        if (candidatos.Count == 0)
        {
            var masVendida = porCantidad.Where(x => EsPaquete(x.Cantidad)).Select(x => (Cantidad?)Cantidad.Unidades(x.Cantidad)).FirstOrDefault();
            return new ResultadoMayoreo(null, masVendida is null ? SinPaquete : PocasVentas, validas.Count, texto, masVendida);
        }

        var (paquete, aFavor) = candidatos[0];
        if (candidatos.Count > 1 && candidatos[1].AFavor == aFavor)
            return new ResultadoMayoreo(null, Empate, validas.Count, texto, null);
        var sugerida = Cantidad.Unidades(paquete);
        if (aFavor < validas.Count * ParteMinima)
            return new ResultadoMayoreo(null, SinPaquete, validas.Count, texto, sugerida);
        return new ResultadoMayoreo(sugerida, null, validas.Count, texto, sugerida);
    }

    /// <summary>Un paquete es de más de una pieza y de piezas enteras.</summary>
    private static bool EsPaquete(decimal cantidad) => cantidad > 1 && cantidad == Math.Floor(cantidad);
}
