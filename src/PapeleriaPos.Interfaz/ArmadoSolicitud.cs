using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Interfaz;

/// <summary>Lo que la caja manda de un ticket: qué y cuánto, el precio solo si se cambió, y el descuento con su motivo.</summary>
public static class ArmadoSolicitud
{
    /// <summary>La línea "Impresiones" no se manda: el servicio la arma con los trabajos y sus precios.</summary>
    public static List<LineaSolicitada> Lineas(Venta venta) => venta.Lineas.Where(l => !l.EsImpresiones).Select(Linea).ToList();

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
