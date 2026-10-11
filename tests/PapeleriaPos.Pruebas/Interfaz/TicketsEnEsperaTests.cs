using Microsoft.Data.Sqlite;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Interfaz;

/// <summary>Los tickets en espera se guardan en la base local de la caja y vuelven iguales después de un reinicio.</summary>
public sealed class TicketsEnEsperaTests : IDisposable
{
    private static readonly Guid Matriz = Ids.Nuevo();
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), $"espera-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _ruta, _ruta + "-wal", _ruta + "-shm" })
            File.Delete(archivo);
    }

    /// <summary>Por pieza a $10.00, desde 12 a $8.00, y la caja de 12 a $90.00.</summary>
    private static ProductoVentaDto Lapicero()
    {
        var variante = Ids.Nuevo();
        var pieza = new PresentacionDto(Ids.Nuevo(), "Pieza", 1000, true);
        var caja = new PresentacionDto(Ids.Nuevo(), "Caja 12", 12000, false);
        return new ProductoVentaDto(variante, "Lapicero azul", "750100", Impuesto.Iva16, UnidadBase.Pieza, true, 40, pieza, [pieza, caja],
        [
            new PrecioDto(Ids.Nuevo(), null, null, 1000, 1000),
            new PrecioDto(Ids.Nuevo(), null, null, 12000, 800),
            new PrecioDto(Ids.Nuevo(), caja.Id, null, 1000, 9000),
        ]);
    }

    private static Venta Vacia() => new() { SucursalId = Matriz, Serie = "A", CreadaUtc = DateTime.UtcNow };

    private static object Resumen(Venta v) => new
    {
        v.TotalCentavos,
        v.IvaCentavos,
        v.ClienteId,
        v.MayoreoDeCliente,
        v.AutorizacionDescuentoId,
        Lineas = string.Join(" | ", v.Lineas.Select(l =>
            $"{l.VarianteId} {l.PresentacionId} {l.Descripcion} {l.Cantidad.Milesimas} {l.PrecioCentavos} {l.PrecioOriginalCentavos} {l.AutorizacionPrecioId} "
            + $"{l.DescuentoCentesimas} {l.DescuentoCentavos} {l.MotivoDescuento} {l.ImporteCentavos} {l.EsImpresiones}")),
    };

    [Fact]
    public void Un_ticket_con_mayoreo_precio_cambiado_descuento_y_articulo_comun_vuelve_igual()
    {
        var lapicero = Lapicero();
        var cuaderno = Lapicero() with { Descripcion = "Cuaderno raya", Codigo = "750200" };
        var escuela = new ClienteRegistradoDto(Ids.Nuevo(), "Escuela", 500, null, true);
        var autorizacion = Ids.Nuevo();
        var venta = Vacia();
        venta.ClienteId = escuela.Id;
        venta.AgregarProducto(lapicero.ParaVenta(), lapicero.PresentacionModelo(), Cantidad.Piezas(15), lapicero.PreciosModelo());
        venta.AgregarProducto(lapicero.ParaVenta(), lapicero.PresentacionModelo(lapicero.Presentaciones[1].Id), Cantidad.Piezas(1), lapicero.PreciosModelo());
        var cambiada = venta.AgregarProducto(cuaderno.ParaVenta(), cuaderno.PresentacionModelo(), Cantidad.Piezas(2), cuaderno.PreciosModelo());
        venta.CambiarPrecio(cambiada, 700, autorizacion);
        venta.DescontarLinea(venta.Lineas[0], 500, null, escuela.MotivoDescuento());
        venta.DescontarLinea(cambiada, null, 150, "Dañado");
        venta.AgregarArticuloComun("Recuerdo", 2500, Cantidad.Piezas(1));
        venta.AutorizacionDescuentoId = Ids.Nuevo();
        var productos = new Dictionary<Guid, ProductoVentaDto> { [lapicero.VarianteId] = lapicero, [cuaderno.VarianteId] = cuaderno };

        CajaReiniciada().GuardarEnEspera([TicketsEnEspera.Guardar(venta, productos, [], escuela, null, fiadoSinConfirmar: true)]);
        // Se reinicia la caja: la base local se abre de nuevo.
        var guardado = Assert.Single(CajaReiniciada().TicketsEnEspera());
        var rearmada = Vacia();
        var perdidas = TicketsEnEspera.Rearmar(guardado, rearmada);

        Assert.Equal(0, perdidas);
        Assert.Equal(venta.Id, rearmada.Id);
        Assert.Equal(venta.CreadaUtc, rearmada.CreadaUtc);
        Assert.Equal(Resumen(venta).ToString(), Resumen(rearmada).ToString());
        Assert.Equal(escuela, guardado.Cliente);
        Assert.True(guardado.FiadoSinConfirmar);
        Assert.Equal([lapicero.VarianteId, cuaderno.VarianteId], guardado.Productos.Select(p => p.VarianteId));
    }

    [Fact]
    public void Las_impresiones_vuelven_en_su_linea_y_un_producto_sin_datos_se_cuenta_como_perdido()
    {
        var lapicero = Lapicero();
        var venta = Vacia();
        venta.AgregarProducto(lapicero.ParaVenta(), lapicero.PresentacionModelo(), Cantidad.Piezas(3), lapicero.PreciosModelo());
        var trabajo = new ImpresionPorCobrarDto(Ids.Nuevo(), "Tarea.pdf", "PC-1", DateTime.Now, 4, 4, false, "Carta", 800, null, null, null);
        venta.FijarImpresiones(trabajo.PrecioCentavos, 0);

        var conTodo = TicketsEnEspera.Guardar(venta, new Dictionary<Guid, ProductoVentaDto> { [lapicero.VarianteId] = lapicero }, [trabajo], null, null, false);
        var rearmada = Vacia();
        Assert.Equal(0, TicketsEnEspera.Rearmar(conTodo, rearmada));
        Assert.Equal(Resumen(venta).ToString(), Resumen(rearmada).ToString());
        Assert.Equal([trabajo.Id], conTodo.Impresiones.Select(i => i.Id));

        var sinProducto = conTodo with { Productos = [] };
        var incompleta = Vacia();
        Assert.Equal(1, TicketsEnEspera.Rearmar(sinProducto, incompleta));
        Assert.Equal(["Impresiones"], incompleta.Lineas.Select(l => l.Descripcion));
    }

    [Fact]
    public void Sin_tickets_o_al_empezar_de_cero_no_queda_ninguno()
    {
        var venta = Vacia();
        venta.AgregarArticuloComun("Recuerdo", 2500, Cantidad.Piezas(1));
        TicketGuardado[] uno = [TicketsEnEspera.Guardar(venta, new Dictionary<Guid, ProductoVentaDto>(), [], null, null, false)];
        var caja = CajaReiniciada();

        caja.GuardarEnEspera(uno);
        caja.GuardarEnEspera([]);
        Assert.Empty(CajaReiniciada().TicketsEnEspera());
        Assert.Null(new AlmacenLocal(_ruta).Leer<List<TicketGuardado>>("espera"));

        caja.GuardarEnEspera(uno);
        _contingencia!.OlvidarCredito();
        Assert.Empty(CajaReiniciada().TicketsEnEspera());
    }

    private Contingencia? _contingencia;

    /// <summary>La caja como al arrancar: su base local abierta de nuevo (sin revisar la conexión: no hace falta aquí).</summary>
    private ClienteCaja CajaReiniciada()
    {
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost:9") };
        var http = new HttpClient();
        _contingencia = new Contingencia(new AlmacenLocal(_ruta), http, conexion);
        return new ClienteCaja(http, conexion, _contingencia);
    }

    [Fact]
    public void Sin_base_local_no_se_guardan_y_no_falla()
    {
        var caja = new ClienteCaja(new HttpClient(), new ConexionCaja { Servicio = new Uri("http://localhost:9") });

        caja.GuardarEnEspera([]);

        Assert.Empty(caja.TicketsEnEspera());
    }
}
