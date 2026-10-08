using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Interfaz;

/// <summary>
/// Poner o quitar al cliente registrado de un ticket (fase 3, entrega 2): su descuento en todos los renglones y su mayoreo
/// siempre. Lo usan CTRL+L, CTRL+D (al quitar los descuentos a mano) y la pantalla de venta cuando se edita al cliente.
/// </summary>
public static class ClienteEnTicket
{
    /// <summary>La presentación y los precios de un renglón, para volver a calcularlo con o sin el mayoreo del cliente.</summary>
    public delegate (Presentacion Presentacion, IReadOnlyList<Precio> Precios)? Catalogo(VentaLinea linea);

    /// <summary>Sale el <paramref name="anterior"/> (su descuento y su mayoreo) y entra el <paramref name="nuevo"/>, si hay.</summary>
    public static void Cambiar(Venta venta, ClienteRegistradoDto? anterior, ClienteRegistradoDto? nuevo, Catalogo catalogo)
    {
        if (anterior is not null)
        {
            foreach (var linea in venta.Lineas.Where(l => l.MotivoDescuento == anterior.MotivoDescuento()).ToList())
                venta.QuitarDescuento(linea);
        }
        var mayoreo = nuevo?.MayoreoSiempre == true;
        if (venta.MayoreoDeCliente != mayoreo)
            venta.CambiarMayoreoDeCliente(mayoreo, l => catalogo(l));
        venta.ClienteId = nuevo?.Id;
        if (nuevo is null)
            return;
        if (nuevo.DescuentoCentesimas > 0 && venta.Lineas.Any(l => !l.EsImpresiones))
            venta.DescontarTicket(nuevo.DescuentoCentesimas, null, nuevo.MotivoDescuento());
        // Lo que queda con descuento es el del cliente, ya aprobado; un descuento a mano se volvería a pedir.
        if (venta.DescuentoARevisar(nuevo.DescuentoCentesimas > 0 ? nuevo.DescuentoCentesimas : null) == 0)
            venta.AutorizacionDescuentoId = null;
    }

    /// <summary>Los renglones que se quedaron sin descuento (se quitó el que se puso a mano) vuelven a llevar el del cliente.</summary>
    public static void DevolverDescuento(Venta venta, ClienteRegistradoDto? cliente)
    {
        if (cliente is not { DescuentoCentesimas: > 0 })
            return;
        foreach (var linea in venta.Lineas.Where(l => !l.EsImpresiones && !l.TieneDescuento).ToList())
            venta.DescontarLinea(linea, cliente.DescuentoCentesimas, null, cliente.MotivoDescuento());
    }
}
