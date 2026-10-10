using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>Poner, cambiar y quitar al cliente de un ticket en la caja (CTRL+L, CTRL+D y el módulo Clientes).</summary>
public class ClienteEnTicketTests
{
    private static readonly Guid Matriz = Ids.Nuevo();
    private readonly ProductoEnVenta _pluma = new(Ids.Nuevo(), "Pluma", Impuesto.Iva16, UnidadBase.Pieza, true);
    private readonly Presentacion _pieza;
    private readonly List<Precio> _precios;

    public ClienteEnTicketTests()
    {
        _pieza = new Presentacion { VarianteId = _pluma.VarianteId, Nombre = "Pieza", EsBase = true };
        _precios =
        [
            new() { VarianteId = _pluma.VarianteId, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = 1000 },
            new() { VarianteId = _pluma.VarianteId, CantidadMinima = Cantidad.Piezas(12), PrecioCentavos = 800 },
        ];
    }

    private (Presentacion, IReadOnlyList<Precio>)? Catalogo(VentaLinea l) => (_pieza, _precios);

    private static ClienteRegistradoDto Cliente(string nombre, int descuento, bool mayoreo = false) =>
        new(Ids.Nuevo(), nombre, descuento, null, true, MayoreoSiempre: mayoreo);

    [Fact]
    public void Cambiar_de_cliente_quita_el_descuento_y_el_mayoreo_del_anterior()
    {
        var venta = new Venta { SucursalId = Matriz, Serie = "A" };
        var linea = venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(10), _precios);
        var escuela = Cliente("Escuela", 1000, mayoreo: true);
        var taller = Cliente("Taller", 500);

        ClienteEnTicket.Cambiar(venta, null, escuela, Catalogo);
        var conEscuela = (linea.PrecioCentavos, linea.DescuentoCentesimas, venta.TotalCentavos);
        ClienteEnTicket.Cambiar(venta, escuela, taller, Catalogo);
        var conTaller = (linea.PrecioCentavos, linea.DescuentoCentesimas, linea.MotivoDescuento, venta.ClienteId);
        ClienteEnTicket.Cambiar(venta, taller, null, Catalogo);

        Assert.Equal((800L, (int?)1000, 7200L), conEscuela);
        Assert.Equal((1000L, (int?)500, "Cliente: Taller", (Guid?)taller.Id), conTaller);
        Assert.Equal((false, (Guid?)null, 10000L), (linea.TieneDescuento, venta.ClienteId, venta.TotalCentavos));
    }

    [Fact]
    public void Al_quitar_un_descuento_a_mano_vuelve_el_del_cliente()
    {
        var venta = new Venta { SucursalId = Matriz, Serie = "A" };
        var linea = venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(10), _precios);
        var escuela = Cliente("Escuela", 1000);
        ClienteEnTicket.Cambiar(venta, null, escuela, Catalogo);
        venta.DescontarLinea(linea, 2000, null, "Dañada");

        venta.QuitarDescuentos(salvoMotivo: escuela.MotivoDescuento());
        ClienteEnTicket.DevolverDescuento(venta, escuela);

        Assert.Equal(((int?)1000, "Cliente: Escuela", 9000L), (linea.DescuentoCentesimas, linea.MotivoDescuento, venta.TotalCentavos));
    }

    [Fact]
    public void Una_venta_con_mayoreo_de_cliente_imprime_lo_que_llevo_aunque_hoy_tenga_otros_privilegios()
    {
        var venta = new Venta { SucursalId = Matriz, Serie = "A" };
        venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(2), _precios);
        var escuela = Cliente("Escuela", 1000, mayoreo: true);
        ClienteEnTicket.Cambiar(venta, null, escuela, Catalogo);
        venta.Pagar(1, [new CobroSolicitado(FormaPago.Efectivo, 100000)], DateTime.UtcNow);

        // Hoy ya no tiene descuento ni mayoreo, pero la venta sí los llevó.
        var ticket = ArmadoTicket.Desde(venta, "Caja 1", "Ana", _ => null, [], [], null, escuela with { DescuentoCentesimas = 0, MayoreoSiempre = false });

        Assert.Equal("Escuela (descuento 10 % · mayoreo siempre)", ticket.Cliente);
    }
}
