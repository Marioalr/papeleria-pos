using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Interfaz;

/// <summary>Lo que la caja manda de un ticket: qué y cuánto, el precio solo si se cambió, y el descuento con su motivo.</summary>
public static class ArmadoSolicitud
{
    /// <summary>La línea "Impresiones" no se manda: el servicio la arma con los trabajos y sus precios.</summary>
    public static List<LineaSolicitada> Lineas(Venta venta) => venta.Lineas.Where(l => !l.EsImpresiones).Select(Linea).ToList();

    /// <summary>
    /// Para cobrar (entrega 5): cada línea con la versión del catálogo de la que salieron los precios que vio el cajero (la del
    /// producto con que se puso el precio), y la del ticket: la menor de ellas, o la de la copia de la caja si no se sabe
    /// ninguna. Sin copia ni versiones (el navegador sin base local, un servicio de antes), sin versión: se cobra como siempre.
    /// </summary>
    public static (List<LineaSolicitada> Lineas, long? Version) ParaCobrar(Venta venta, IReadOnlyDictionary<Guid, ProductoVentaDto> productos, long? deLaCopia)
    {
        var lineas = venta.Lineas.Where(l => !l.EsImpresiones)
            .Select(l => Linea(l) with { VersionCatalogo = l.VarianteId is { } v ? productos.GetValueOrDefault(v)?.Version : null })
            .ToList();
        var conocidas = lineas.Select(l => l.VersionCatalogo).OfType<long>().ToList();
        return (lineas, conocidas.Count > 0 ? conocidas.Min() : deLaCopia);
    }

    public static LineaSolicitada Linea(VentaLinea l)
    {
        var linea = l switch
        {
            { VarianteId: null } => new LineaSolicitada(null, null, l.Cantidad.Milesimas, l.Descripcion, l.PrecioCentavos),
            { PrecioCambiado: true } => new LineaSolicitada(l.VarianteId, l.PresentacionId, l.Cantidad.Milesimas, null, l.PrecioCentavos, l.AutorizacionPrecioId),
            _ => new LineaSolicitada(l.VarianteId, l.PresentacionId, l.Cantidad.Milesimas),
        };
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
