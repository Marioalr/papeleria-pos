using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Lo que la caja manda de un ticket: qué y cuánto, el precio solo si se cambió, el descuento con su motivo y la promoción que
/// aplicó, con lo que se ahorró (fase 3: el servicio revisa que sea la misma).
/// </summary>
public static class ArmadoSolicitud
{
    /// <summary>La línea "Impresiones" no se manda: el servicio la arma con los trabajos y sus precios.</summary>
    /// <param name="conDescuentoEnPausa">
    /// Para guardar el ticket en espera: también el descuento del cliente que está en pausa por una promoción (fase 3), para que
    /// vuelva igual. Al cobrar no va: no descuenta nada.
    /// </param>
    public static List<LineaSolicitada> Lineas(Venta venta, bool conDescuentoEnPausa = false) =>
        venta.Lineas.Where(l => !l.EsImpresiones).Select(l => Linea(l, conDescuentoEnPausa)).ToList();

    /// <summary>
    /// Para cobrar (entrega 5): cada línea con la versión del catálogo de la que salió el precio que vio el cajero en ella
    /// (<paramref name="versionDe"/>: la del producto con que se agregó o se volvió a calcular esa línea; dos presentaciones
    /// del mismo producto pueden venir de copias distintas), y la del ticket: la menor de ellas, o la de la copia de la caja
    /// si no se sabe ninguna. Sin copia ni versiones (el navegador sin base local, un servicio de antes), sin versión: se
    /// cobra como siempre.
    /// </summary>
    public static (List<LineaSolicitada> Lineas, long? Version) ParaCobrar(Venta venta, Func<VentaLinea, long?> versionDe, long? deLaCopia)
    {
        var lineas = venta.Lineas.Where(l => !l.EsImpresiones)
            .Select(l => Linea(l) with { VersionCatalogo = l.VarianteId is null ? null : versionDe(l) })
            .ToList();
        var conocidas = lineas.Select(l => l.VersionCatalogo).OfType<long>().ToList();
        return (lineas, conocidas.Count > 0 ? conocidas.Min() : deLaCopia);
    }

    public static LineaSolicitada Linea(VentaLinea l) => Linea(l, conDescuentoEnPausa: false);

    private static LineaSolicitada Linea(VentaLinea l, bool conDescuentoEnPausa)
    {
        var linea = l switch
        {
            { VarianteId: null } => new LineaSolicitada(null, null, l.Cantidad.Milesimas, l.Descripcion, l.PrecioCentavos),
            { PrecioCambiado: true } => new LineaSolicitada(l.VarianteId, l.PresentacionId, l.Cantidad.Milesimas, null, l.PrecioCentavos, l.AutorizacionPrecioId),
            _ => new LineaSolicitada(l.VarianteId, l.PresentacionId, l.Cantidad.Milesimas),
        };
        if (l.TienePromocion)
            linea = linea with { PromocionId = l.PromocionId, AhorroPromocionCentavos = l.AhorroPromocionCentavos, Promocion = l.Promocion };
        if (conDescuentoEnPausa && l.DescuentoDeCliente && !l.TieneDescuento)
            return linea with { DescuentoCentesimas = l.DescuentoCentesimas, MotivoDescuento = l.MotivoDescuento };
        return l.TieneDescuento
            ? linea with
            {
                DescuentoCentesimas = l.DescuentoCentesimas,
                DescuentoCentavos = l.DescuentoCentesimas is null ? l.DescuentoCentavos : null,
                MotivoDescuento = l.MotivoDescuento,
            }
            : linea;
    }
}
