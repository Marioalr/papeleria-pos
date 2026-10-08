using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Clientes;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Usuarios;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Nucleo;

/// <summary>El cliente registrado en la venta (fase 3, entrega 2): mayoreo siempre, su descuento y cambiar de cliente.</summary>
public class ClienteRegistradoTests
{
    private static readonly Guid Matriz = Ids.Nuevo();

    private readonly ProductoEnVenta _pluma = new(Ids.Nuevo(), "Pluma punto fino azul", Impuesto.Iva16, UnidadBase.Pieza, true);
    private readonly ProductoEnVenta _goma = new(Ids.Nuevo(), "Goma blanca", Impuesto.Iva16, UnidadBase.Pieza, true);
    private readonly Presentacion _pieza;
    private readonly Presentacion _caja12;
    private readonly Presentacion _gomaPieza;
    private readonly List<Precio> _precios;

    public ClienteRegistradoTests()
    {
        _pieza = new Presentacion { VarianteId = _pluma.VarianteId, Nombre = "Pieza", EsBase = true };
        _caja12 = new Presentacion { VarianteId = _pluma.VarianteId, Nombre = "Caja 12", Factor = Cantidad.Piezas(12) };
        _gomaPieza = new Presentacion { VarianteId = _goma.VarianteId, Nombre = "Pieza", EsBase = true };
        _precios =
        [
            new() { VarianteId = _pluma.VarianteId, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = 1000 },
            new() { VarianteId = _pluma.VarianteId, CantidadMinima = Cantidad.Piezas(12), PrecioCentavos = 800 },
            new() { VarianteId = _pluma.VarianteId, CantidadMinima = Cantidad.Piezas(24), PrecioCentavos = 750 },
            new() { VarianteId = _goma.VarianteId, CantidadMinima = Cantidad.Piezas(1), PrecioCentavos = 500 },
        ];
    }

    private Venta NuevaVenta() => new() { SucursalId = Matriz, Serie = "A", CreadaUtc = DateTime.UtcNow };

    private (Presentacion, IReadOnlyList<Precio>)? Catalogo(VentaLinea l) =>
        l.VarianteId == _pluma.VarianteId ? (l.PresentacionId == _caja12.Id ? _caja12 : _pieza, _precios)
        : l.VarianteId == _goma.VarianteId ? (_gomaPieza, _precios) : null;

    [Theory]
    [InlineData(1, 800)]   // desde la primera pieza, la primera escala de mayoreo
    [InlineData(12, 800)]
    [InlineData(24, 750)]  // la escala más alta sigue al llegar a ella
    public void Mayoreo_siempre_es_al_menos_la_primera_escala_de_mayoreo(int piezas, long precio)
    {
        var aplicado = CalculoPrecio.Calcular(_precios, Matriz, _pieza, Cantidad.Piezas(piezas), mayoreoSiempre: true)!;

        Assert.Equal(precio, aplicado.Precio.PrecioCentavos);
        Assert.True(aplicado.EsMayoreo);
    }

    [Fact]
    public void Sin_escalas_de_mayoreo_el_mayoreo_siempre_no_cambia_nada()
    {
        var aplicado = CalculoPrecio.Calcular(_precios, Matriz, _gomaPieza, Cantidad.Piezas(3), mayoreoSiempre: true)!;

        Assert.Equal((500L, false), (aplicado.Precio.PrecioCentavos, aplicado.EsMayoreo));
    }

    [Fact]
    public void Elegir_al_cliente_con_mayoreo_vuelve_a_calcular_los_renglones_y_quitarlo_los_regresa()
    {
        var venta = NuevaVenta();
        var plumas = venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(3), _precios);
        var goma = venta.AgregarProducto(_goma, _gomaPieza, Cantidad.Piezas(2), _precios);

        venta.CambiarMayoreoDeCliente(true, Catalogo);
        var conMayoreo = (plumas.PrecioCentavos, plumas.MayoreoDeCliente, plumas.AhorroMayoreoCentavos, goma.PrecioCentavos, venta.TotalCentavos);
        var otra = venta.AgregarProducto(_pluma, _caja12, Cantidad.Piezas(2), _precios);  // 24 piezas: ya llega a la escala de 24
        venta.CambiarMayoreoDeCliente(false, Catalogo);

        Assert.Equal((800L, true, 600L, 500L, 3400L), conMayoreo);
        Assert.Equal((Cantidad.Piezas(24), false), (otra.MayoreoDesde!.Value, otra.MayoreoDeCliente));
        Assert.Equal((1000L, false, 0L), (plumas.PrecioCentavos, plumas.TieneMayoreo, plumas.AhorroMayoreoCentavos));
    }

    [Fact]
    public void Quitar_los_descuentos_a_mano_deja_el_del_cliente()
    {
        var venta = NuevaVenta();
        var plumas = venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(3), _precios);
        var goma = venta.AgregarProducto(_goma, _gomaPieza, Cantidad.Piezas(2), _precios);
        venta.DescontarTicket(1000, null, "Cliente: Escuela");
        venta.DescontarLinea(goma, 2000, null, "Goma maltratada");
        venta.AutorizacionDescuentoId = Ids.Nuevo();
        venta.ClienteId = Ids.Nuevo();

        venta.QuitarDescuentos(salvoMotivo: "Cliente: Escuela");

        Assert.Equal(("Cliente: Escuela", false, (Guid?)null), (plumas.MotivoDescuento, goma.TieneDescuento, venta.AutorizacionDescuentoId));
        Assert.NotNull(venta.ClienteId);
    }

    [Fact]
    public void El_ticket_dice_el_cliente_con_sus_privilegios_y_el_mayoreo_de_cliente()
    {
        var venta = NuevaVenta();
        venta.MayoreoDeCliente = true;
        venta.AgregarProducto(_pluma, _pieza, Cantidad.Piezas(2), _precios);
        venta.Pagar(1, [new CobroSolicitado(FormaPago.Efectivo, 10000)], DateTime.UtcNow);
        var cliente = new ClienteRegistradoDto(Ids.Nuevo(), "Papelería La Esquina", 500, null, true, MayoreoSiempre: true);

        var ticket = ArmadoTicket.Desde(venta, "Caja 1", "Ana", _ => null, [], [], null, cliente);

        // Dice lo que llevó la venta: aquí solo el mayoreo (el descuento no se puso en los renglones).
        Assert.Equal("Papelería La Esquina (mayoreo siempre)", ticket.Cliente);
        Assert.Equal(("Mayoreo de cliente", 400L), (ticket.Lineas.Single().Mayoreo, ticket.Lineas.Single().AhorroMayoreoCentavos));
    }

    [Fact]
    public void El_telefono_se_guarda_solo_con_digitos()
    {
        Assert.Equal("5512345678", ClienteRegistrado.SoloDigitos("(55) 1234-5678"));
        Assert.Null(ClienteRegistrado.SoloDigitos(" - "));
    }

    [Fact]
    public void La_bitacora_dice_los_privilegios_para_avisarle_al_dueno()
    {
        var antes = new ClienteRegistradoDto(Ids.Nuevo(), "Escuela", 500, null, true);

        Assert.Equal("Alta del cliente «Escuela»", RegistroClientes.Detalle(null, antes with { DescuentoCentesimas = 0 }));
        Assert.Equal("Privilegios de «Escuela» (alta): descuento 5 %", RegistroClientes.Detalle(null, antes));
        Assert.Equal("Privilegios de «Escuela»: descuento 5 % → 10 %, mayoreo siempre no → sí",
            RegistroClientes.Detalle(antes, antes with { DescuentoCentesimas = 1000, MayoreoSiempre = true }));
        Assert.Equal("Cliente «Escuela»: lo dejó inactivo", RegistroClientes.Detalle(antes, antes with { Activo = false }));
        Assert.Equal("Editó el cliente «Escuela»", RegistroClientes.Detalle(antes, antes with { Nota = "Recoge la maestra" }));
        // Quien tiene privilegios y cambia de nombre o de teléfono, o vuelve a estar activo: también se le avisa al dueño.
        Assert.Equal("Privilegios de «Juan Pérez»: nombre «Escuela» → «Juan Pérez», teléfono — → 5512345678",
            RegistroClientes.Detalle(antes, antes with { Nombre = "Juan Pérez", Telefono = "5512345678" }));
        Assert.Equal("Privilegios de «Escuela»: lo volvió a activar", RegistroClientes.Detalle(antes with { Activo = false }, antes));
        // Sin privilegios, cambiarle el nombre no es aviso.
        Assert.Equal("Cliente «Arq. R.»: nombre «Arq. Ramírez» → «Arq. R.»",
            RegistroClientes.Detalle(antes with { Nombre = "Arq. Ramírez", DescuentoCentesimas = 0 }, antes with { Nombre = "Arq. R.", DescuentoCentesimas = 0 }));
    }

    [Fact]
    public void El_encargado_administra_los_clientes_y_el_cajero_no()
    {
        Assert.False(Permisos.SoloDelDueno(AccionControlada.AdministrarClientes));
        Assert.Equal(NivelPermiso.Permitido, Permisos.Evaluar(Permisos.Iniciales, Rol.Encargado, Matriz, AccionControlada.AdministrarClientes).Nivel);
        Assert.Equal(NivelPermiso.NoPermitido, Permisos.Evaluar(Permisos.Iniciales, Rol.Cajero, Matriz, AccionControlada.AdministrarClientes).Nivel);
    }
}
