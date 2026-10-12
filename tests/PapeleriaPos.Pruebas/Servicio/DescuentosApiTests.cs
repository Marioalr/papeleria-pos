using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

public class DescuentosApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>
{
    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<(HttpClient Http, CajaDto Caja, SesionDto Cajero, TurnoDto Turno, ProductoVentaDto Producto)> PrepararAsync()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var respuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
        var buscado = (await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q=cuaderno&sucursalId={caja.SucursalId}"))!
            .First(r => r.Codigo is not null && r.Existencia > 5);
        var producto = await Leer<ProductoVentaDto>(await http.GetAsync($"api/productos/codigo/{buscado.Codigo}?sucursalId={caja.SucursalId}"));
        return (http, caja, cajero, turno, producto);
    }

    private static SolicitudCobro Cobro(CajaDto caja, TurnoDto turno, SesionDto cajero, LineaSolicitada linea, Guid? cliente = null, Guid? autorizacion = null,
        Guid? bajoCosto = null) =>
        // Un billete de $1,000 alcanza para uno o dos cuadernos (más de $2,000 de cambio no se cobra).
        new(caja.Id, turno.Id, cajero.UsuarioId, [linea], [new CobroSolicitado(FormaPago.Efectivo, 100_000)], Guid.NewGuid(),
            ClienteId: cliente, AutorizacionDescuentoId: autorizacion, AutorizacionBajoCostoId: bajoCosto);

    private List<Bitacora> Bitacora(Guid ventaId)
    {
        using var alcance = servicio.Fabrica.Services.CreateScope();
        return alcance.ServiceProvider.GetRequiredService<PosDbContext>().Bitacora.AsNoTracking().Where(b => b.VentaId == ventaId).ToList();
    }

    [Fact]
    public async Task El_cajero_da_hasta_10_por_ciento_solo_y_el_ticket_lo_muestra()
    {
        var (http, caja, cajero, turno, producto) = await PrepararAsync();
        var linea = new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 2000, DescuentoCentesimas: 1000, MotivoDescuento: "Cliente frecuente");

        var ticket = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", Cobro(caja, turno, cajero, linea)));

        var renglon = Assert.Single(ticket.Lineas);
        var bruto = renglon.ImporteCentavos + renglon.DescuentoCentavos;
        Assert.Equal(Redondeo(bruto, 1000), renglon.DescuentoCentavos);
        Assert.Equal("10 %", renglon.Descuento);
        Assert.Equal(ticket.TotalCentavos, renglon.ImporteCentavos);
        var registro = Assert.Single(Bitacora(ticket.VentaId!.Value), b => b.Accion == AccionControlada.Descuento);
        Assert.Equal(ResultadoBitacora.Hecho, registro.Resultado);
        Assert.Equal(renglon.DescuentoCentavos, registro.MontoCentavos);
        Assert.Contains("Cliente frecuente", registro.Detalle);
    }

    [Fact]
    public async Task Mas_de_10_por_ciento_pide_autorizacion_y_queda_en_la_bitacora()
    {
        var (http, caja, cajero, turno, producto) = await PrepararAsync();
        var linea = new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 1000, DescuentoCentesimas: 1500, MotivoDescuento: "Pasta doblada");

        var sinAutorizacion = await http.PostAsJsonAsync("api/ventas", Cobro(caja, turno, cajero, linea));
        var poco = await Leer<AutorizacionDto>(await http.PostAsJsonAsync("api/autorizaciones", new SolicitudAutorizacion(AccionControlada.Descuento,
            caja.SucursalId, caja.Id, cajero.UsuarioId, "encargado", "2222", 1200, "Descuento")));
        var conPoca = await http.PostAsJsonAsync("api/ventas", Cobro(caja, turno, cajero, linea, autorizacion: poco.Id));
        var suficiente = await Leer<AutorizacionDto>(await http.PostAsJsonAsync("api/autorizaciones", new SolicitudAutorizacion(AccionControlada.Descuento,
            caja.SucursalId, caja.Id, cajero.UsuarioId, "encargado", "2222", 1500, "Descuento")));
        var ticket = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", Cobro(caja, turno, cajero, linea, autorizacion: suficiente.Id)));

        Assert.Equal(HttpStatusCode.Forbidden, sinAutorizacion.StatusCode);
        Assert.Equal("requiere-autorizacion", (await sinAutorizacion.Content.ReadFromJsonAsync<ErrorDto>())!.Codigo);
        Assert.Equal(HttpStatusCode.BadRequest, conPoca.StatusCode);
        Assert.Contains("mayor al que se autorizó", await conPoca.Content.ReadAsStringAsync());
        var registro = Assert.Single(Bitacora(ticket.VentaId!.Value), b => b.Accion == AccionControlada.Descuento);
        Assert.Equal(ResultadoBitacora.Autorizado, registro.Resultado);
        Assert.Contains("15 %", registro.Detalle);
    }

    [Fact]
    public async Task El_descuento_del_cliente_registrado_no_pide_autorizacion_y_un_cliente_inactivo_ya_no_se_cobra()
    {
        var (http, caja, cajero, turno, producto) = await PrepararAsync();
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var escuela = await Leer<ClienteRegistradoDto>(await http.PostAsJsonAsync("api/clientes", new SolicitudGuardarCliente(encargado.UsuarioId,
            caja.SucursalId, new ClienteRegistradoDto(Guid.Empty, "Escuela Benito Juárez (descuentos)", 2000, "Maestros con credencial", true))));
        var linea = new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 1000, DescuentoCentesimas: 2000,
            MotivoDescuento: escuela.MotivoDescuento());
        var ticket = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", Cobro(caja, turno, cajero, linea, cliente: escuela.Id)));
        var paquete = await http.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{caja.Id}/paquete");

        Assert.Contains("Escuela Benito Juárez (descuentos), cliente registrado",
            Assert.Single(Bitacora(ticket.VentaId!.Value), b => b.Accion == AccionControlada.Descuento).Detalle);
        Assert.Equal("Escuela Benito Juárez (descuentos) (descuento 20 %)", ticket.Cliente);
        Assert.Contains(paquete!.Clientes!, c => c.Id == escuela.Id);

        // Inactivo ya no se le puede cobrar (ni con su descuento ni sin él).
        await Leer<ClienteRegistradoDto>(await http.PostAsJsonAsync("api/clientes",
            new SolicitudGuardarCliente(encargado.UsuarioId, caja.SucursalId, escuela with { Activo = false })));
        var inactivo = await http.PostAsJsonAsync("api/ventas", Cobro(caja, turno, cajero, linea, cliente: escuela.Id));
        Assert.Equal(HttpStatusCode.BadRequest, inactivo.StatusCode);
        Assert.Contains("ya no está activo", (await inactivo.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
    }

    [Fact]
    public async Task Un_descuento_que_deja_la_linea_debajo_del_costo_pide_autorizacion()
    {
        var (http, caja, cajero, turno, producto) = await PrepararAsync();
        var linea = new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 1000, DescuentoCentesimas: 9500, MotivoDescuento: "Remate");
        var descuento = await Leer<AutorizacionDto>(await http.PostAsJsonAsync("api/autorizaciones", new SolicitudAutorizacion(AccionControlada.Descuento,
            caja.SucursalId, caja.Id, cajero.UsuarioId, "encargado", "2222", 9500, "Remate")));

        var revision = await Leer<BajoCostoDto>(await http.PostAsJsonAsync("api/descuentos/bajo-costo", new SolicitudBajoCosto(caja.Id, [linea])));
        var sinCosto = await http.PostAsJsonAsync("api/ventas", Cobro(caja, turno, cajero, linea, autorizacion: descuento.Id));
        var otraVez = await Leer<AutorizacionDto>(await http.PostAsJsonAsync("api/autorizaciones", new SolicitudAutorizacion(AccionControlada.Descuento,
            caja.SucursalId, caja.Id, cajero.UsuarioId, "encargado", "2222", 9500, "Remate")));
        var costo = await Leer<AutorizacionDto>(await http.PostAsJsonAsync("api/autorizaciones", new SolicitudAutorizacion(AccionControlada.VenderBajoCosto,
            caja.SucursalId, caja.Id, cajero.UsuarioId, "encargado", "2222", revision.FaltanteCentavos, "Remate")));
        var ticket = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas",
            Cobro(caja, turno, cajero, linea, autorizacion: otraVez.Id, bajoCosto: costo.Id)));

        Assert.True(revision.FaltanteCentavos > 0);
        Assert.Equal([producto.Descripcion], revision.Lineas);
        Assert.Equal(HttpStatusCode.Forbidden, sinCosto.StatusCode);
        Assert.Contains(Bitacora(ticket.VentaId!.Value), b => b.Accion == AccionControlada.VenderBajoCosto && b.Resultado == ResultadoBitacora.Autorizado);
    }

    [Fact]
    public async Task Al_devolver_se_regresa_lo_que_se_cobro_con_descuento()
    {
        var (http, caja, cajero, turno, producto) = await PrepararAsync();
        var linea = new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 2000, DescuentoCentesimas: 1000, MotivoDescuento: "Cliente frecuente");
        var ticket = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", Cobro(caja, turno, cajero, linea)));
        var venta = await Leer<VentaParaDevolverDto>(await http.GetAsync($"api/sucursales/{caja.SucursalId}/ventas/folio/{ticket.Folio}"));

        var devolucion = await Leer<ComprobanteDevolucionDto>(await http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
            new SolicitudDevolucion(cajero.UsuarioId, caja.Id, "No le gustó", [new LineaDevolucionSolicitada(venta.Lineas.Single().LineaId, 1000)])));

        Assert.Equal(ticket.TotalCentavos / 2, devolucion.TotalCentavos);
    }

    private static long Redondeo(long bruto, int centesimas) => PapeleriaPos.Nucleo.Redondeo.Dividir((Int128)bruto * centesimas, 10000);
}
