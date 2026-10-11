using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Un ticket en espera como se guarda en la base local de la caja: las mismas líneas que se mandan al cobrar
/// (<see cref="ArmadoSolicitud"/>), los productos como estaban al agregarlos, las impresiones y el cliente. Con el mismo
/// Id de la venta: si se mandó a fiar y no llegó la respuesta, se cobra como la misma venta.
/// </summary>
public sealed record TicketGuardado(
    Guid VentaId, DateTime CreadaUtc, List<LineaSolicitada> Lineas, List<ProductoVentaDto> Productos, List<ImpresionPorCobrarDto> Impresiones,
    ClienteRegistradoDto? Cliente, CreditoClienteDto? Cuenta, bool MayoreoDeCliente, Guid? AutorizacionDescuentoId, Guid? AutorizacionBajoCostoId,
    bool FiadoSinConfirmar);

/// <summary>
/// Los tickets en espera sobreviven a un apagón o a un reinicio de la caja (docs/propuesta-interfaz.md, «Tickets en
/// espera»): se guardan cada vez que cambian y se vuelven a armar al entrar a la venta.
/// </summary>
public static class TicketsEnEspera
{
    public static TicketGuardado Guardar(Venta venta, IReadOnlyDictionary<Guid, ProductoVentaDto> productos, IReadOnlyList<ImpresionPorCobrarDto> impresiones,
        ClienteRegistradoDto? cliente, CreditoClienteDto? cuenta, bool fiadoSinConfirmar) =>
        new(venta.Id, venta.CreadaUtc, ArmadoSolicitud.Lineas(venta),
            venta.Lineas.Select(l => l.VarianteId).OfType<Guid>().Distinct().Select(productos.GetValueOrDefault).OfType<ProductoVentaDto>().ToList(),
            [.. impresiones], cliente, cuenta, venta.MayoreoDeCliente, venta.AutorizacionDescuentoId, venta.AutorizacionBajoCostoId, fiadoSinConfirmar);

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
                if (l.PrecioCentavos is { } precio && precio != agregada.PrecioCentavos)
                    venta.CambiarPrecio(agregada, precio, l.AutorizacionId);
                venta.AplicarDescuentoSolicitado(agregada, l.DescuentoCentesimas, l.DescuentoCentavos, l.MotivoDescuento);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                perdidas++;
            }
        }
        if (t.Impresiones.Count > 0)
            venta.FijarImpresiones(t.Impresiones.Sum(i => i.PrecioCentavos), 0);
        venta.AutorizacionDescuentoId = t.AutorizacionDescuentoId;
        venta.AutorizacionBajoCostoId = t.AutorizacionBajoCostoId;
        return perdidas;
    }
}
