using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Un ticket en espera como se guarda en la base local de la caja: las mismas líneas que se mandan al cobrar
/// (<see cref="ArmadoSolicitud"/>), los productos como estaban al agregarlos, las impresiones y el cliente. Con el mismo
/// Id de la venta: si se mandó a fiar y no llegó la respuesta, se cobra como la misma venta.
/// </summary>
/// <param name="Nota">La nota del ticket (F4 al cobrar), si ya se escribió.</param>
/// <param name="ImpresionesControlPrint">Las tarjetas de Control Print que lleva (2026-10-25); al cobrar se vuelven a revisar allá.</param>
public sealed record TicketGuardado(
    Guid VentaId, DateTime CreadaUtc, List<LineaSolicitada> Lineas, List<ProductoVentaDto> Productos, List<ImpresionPorCobrarDto> Impresiones,
    ClienteRegistradoDto? Cliente, CreditoClienteDto? Cuenta, bool MayoreoDeCliente, Guid? AutorizacionDescuentoId, Guid? AutorizacionBajoCostoId,
    bool FiadoSinConfirmar, string? Nota = null, List<TrabajoControlPrintDto>? ImpresionesControlPrint = null);

/// <summary>
/// Los tickets en espera sobreviven a un apagón o a un reinicio de la caja (docs/propuesta-interfaz.md, «Tickets en
/// espera»): se guardan cada vez que cambian y se vuelven a armar al entrar a la venta.
/// </summary>
public static class TicketsEnEspera
{
    public static TicketGuardado Guardar(Venta venta, IReadOnlyDictionary<Guid, ProductoVentaDto> productos, IReadOnlyList<ImpresionPorCobrarDto> impresiones,
        ClienteRegistradoDto? cliente, CreditoClienteDto? cuenta, bool fiadoSinConfirmar, IReadOnlyList<TrabajoControlPrintDto>? controlPrint = null) =>
        new(venta.Id, venta.CreadaUtc, ArmadoSolicitud.Lineas(venta, conDescuentoEnPausa: true),
            venta.Lineas.Where(l => !l.EsImpresiones).Select(l => l.VarianteId).OfType<Guid>().Distinct().Select(productos.GetValueOrDefault)
                .OfType<ProductoVentaDto>().ToList(),
            [.. impresiones], cliente, cuenta, venta.MayoreoDeCliente, venta.AutorizacionDescuentoId, venta.AutorizacionBajoCostoId, fiadoSinConfirmar,
            venta.Nota, controlPrint is { Count: > 0 } ? [.. controlPrint] : null);

    /// <summary>
    /// Vuelve a armar la venta sobre <paramref name="venta"/> (vacía, con la caja, el turno y el cajero de ahora), con los
    /// mismos pasos que la caja y los precios de cuando se agregó cada producto. Lo que ya no se puede armar se deja fuera
    /// y se cuenta.
    /// </summary>
    public static int Rearmar(TicketGuardado t, Venta venta)
    {
        venta.Id = t.VentaId;
        venta.CreadaUtc = t.CreadaUtc;
        // El cliente va antes que las líneas: con mayoreo siempre, sus precios son otros.
        venta.ClienteId = t.Cliente?.Id;
        venta.MayoreoDeCliente = t.Cliente is not null && t.MayoreoDeCliente;
        var productos = t.Productos.ToDictionary(p => p.VarianteId);
        var perdidas = 0;
        foreach (var l in t.Lineas)
        {
            try
            {
                var cantidad = new Cantidad(l.CantidadMilesimas);
                if (l.VarianteId is not { } varianteId)
                {
                    var comun = venta.AgregarArticuloComun(l.Descripcion ?? "", l.PrecioCentavos ?? 0, cantidad);
                    venta.AplicarDescuentoSolicitado(comun, l.DescuentoCentesimas, l.DescuentoCentavos, l.MotivoDescuento);
                    continue;
                }
                if (productos.GetValueOrDefault(varianteId) is not { } producto)
                {
                    perdidas++;
                    continue;
                }
                var agregada = venta.AgregarProducto(producto.ParaVenta(), producto.PresentacionModelo(l.PresentacionId), cantidad, producto.PreciosModelo());
                // Solo lo cambiado con Ctrl+E se guarda con su precio: vuelve cambiado aunque sea el de la lista, sin promoción.
                if (l.PrecioCentavos is { } precio)
                    venta.CambiarPrecio(agregada, precio, l.AutorizacionId);
                venta.AplicarDescuentoSolicitado(agregada, l.DescuentoCentesimas, l.DescuentoCentavos, l.MotivoDescuento);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                perdidas++;
            }
        }
        ImpresionesDelTicket.Fijar(venta, t.Impresiones, t.ImpresionesControlPrint ?? []);
        venta.AutorizacionDescuentoId = t.AutorizacionDescuentoId;
        venta.AutorizacionBajoCostoId = t.AutorizacionBajoCostoId;
        venta.PonerNota(NotaDelTicket.Recortada(t.Nota));
        return perdidas;
    }
}

/// <summary>
/// La línea «Impresiones» del ticket como la arma la caja: las del módulo y las tarjetas de Control Print (2026-10-25), juntas. El
/// costo no viaja a la caja: el servicio pone el de cada trabajo al cobrar (y en el piloto, la línea como IMP).
/// </summary>
public static class ImpresionesDelTicket
{
    /// <summary>
    /// Las dos líneas de impresiones del ticket (2026-10-29): las impresiones y, aparte, las copias del panel de la impresora (las
    /// registradas en F2 y las de Control Print de ese tipo), como las arma el servicio al cobrar. Regresa la primera que quedó.
    /// </summary>
    public static VentaLinea? Fijar(Venta venta, IReadOnlyCollection<ImpresionPorCobrarDto> delModulo, IReadOnlyCollection<TrabajoControlPrintDto> deControlPrint)
    {
        VentaLinea? primera = null;
        foreach (var copias in new[] { false, true })
        {
            var (modulo, controlPrint) = DeLaLinea(delModulo, deControlPrint, copias);
            var linea = venta.FijarImpresiones(modulo.Sum(i => i.PrecioCentavos) + controlPrint.Sum(t => t.PrecioCentavos), 0,
                LineaDeImpresiones.Descripcion(controlPrint.Select(t => t.Id), modulo.Select(i => i.Pc), conReferencias: false, copias), copias: copias);
            primera ??= linea;
        }
        return primera;
    }

    /// <summary>Lo de una de las dos líneas: las copias del panel o lo demás.</summary>
    public static (List<ImpresionPorCobrarDto> DelModulo, List<TrabajoControlPrintDto> DeControlPrint) DeLaLinea(
        IEnumerable<ImpresionPorCobrarDto> delModulo, IEnumerable<TrabajoControlPrintDto> deControlPrint, bool copias) =>
        (delModulo.Where(i => (i.Tipo == TipoTrabajoImpresion.Copia) == copias).ToList(), deControlPrint.Where(t => t.CopiasEnImpresora == copias).ToList());

    /// <summary>Lo que dice el renglón: con los números de Control Print, la línea; si no, «Impresiones (3)» o «Copias (2)», como siempre.</summary>
    public static string Renglon(VentaLinea linea, IReadOnlyCollection<ImpresionPorCobrarDto> delModulo, IReadOnlyCollection<TrabajoControlPrintDto> deControlPrint)
    {
        var (modulo, controlPrint) = DeLaLinea(delModulo, deControlPrint, linea.EsCopias);
        return linea.Descripcion is LineaDeImpresiones.Nombre or LineaDeImpresiones.NombreCopias
            ? $"{linea.Descripcion} ({modulo.Count + controlPrint.Count})"
            : linea.Descripcion;
    }
}
