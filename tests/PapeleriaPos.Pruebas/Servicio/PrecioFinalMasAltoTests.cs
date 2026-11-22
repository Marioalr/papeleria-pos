using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// «Precio y descuento» en una sola ventana (dueño, 2026-11-22), del lado del servicio y de la contingencia: el «Precio final» más alto
/// que lo que se cobra llega como un precio cambiado (como Ctrl+E antes), y así se revisa (el permiso de «Cambiar precio»), se anota en
/// la bitácora y queda en la venta: con su precio de lista aparte y sin descuento, para que los reportes y el margen lo vean como precio.
/// </summary>
public sealed class PrecioFinalMasAltoTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-precio-final-{Guid.NewGuid():N}.db");

    public void Dispose() => ArchivosDePrueba.BorrarBase(_baseLocal);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private T EnLaBase<T>(Func<PosDbContext, T> leer)
    {
        using var alcance = servicio.Fabrica.Services.CreateScope();
        return leer(alcance.ServiceProvider.GetRequiredService<PosDbContext>());
    }

    /// <summary>La línea que manda la pantalla después de subirle el precio (Venta.SubirPrecio y ArmadoSolicitud), como la armaría ella.</summary>
    private static (LineaSolicitada Linea, long Precio, long Lista) Subida(ProductoVentaDto producto, long piezas, long masCentavos, Guid? autorizacion = null)
    {
        var venta = new Venta { Serie = "A" };
        var linea = venta.AgregarProducto(producto.ParaVenta(), producto.PresentacionModelo(), PapeleriaPos.Nucleo.Cantidad.Piezas(piezas), producto.PreciosModelo());
        var lista = linea.PrecioCentavos;
        var precio = venta.SubirPrecio(linea, linea.ImporteSinDescuentoCentavos + masCentavos, autorizacion);
        return (Assert.Single(ArmadoSolicitud.Lineas(venta)), precio, lista);
    }

    [Fact]
    public async Task En_linea_el_precio_mas_alto_es_cambio_de_precio_con_su_permiso_y_su_bitacora()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var respuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
        var buscado = (await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q=cuaderno&sucursalId={caja.SucursalId}"))!
            .First(r => r.Codigo is not null && r.Existencia > 5);
        var producto = await Leer<ProductoVentaDto>(await http.GetAsync($"api/productos/codigo/{buscado.Codigo}?sucursalId={caja.SucursalId}"));
        SolicitudCobro Cobro(SesionDto quien, LineaSolicitada linea) =>
            new(caja.Id, turno.Id, quien.UsuarioId, [linea], [new CobroSolicitado(FormaPago.Efectivo, 1_000_000)], Guid.NewGuid());

        // El cajero, sin autorización: el servicio la pide (es «Cambiar precio», no un descuento de su 10 %).
        var (sinAutorizar, _, _) = Subida(producto, 3, 1000);
        var negado = await http.PostAsJsonAsync("api/ventas", Cobro(cajero, sinAutorizar));
        Assert.False(negado.IsSuccessStatusCode);

        // Con la del dueño: se cobra a ese precio, con el de lista aparte y sin descuento; la bitácora lo dice como precio.
        var (paraAutorizar, precio, lista) = Subida(producto, 3, 1000);
        var importe = PapeleriaPos.Nucleo.Cantidad.Piezas(3).Importe(precio);
        var autorizacion = await Leer<AutorizacionDto>(await http.PostAsJsonAsync("api/autorizaciones", new SolicitudAutorizacion(AccionControlada.CambiarPrecio,
            caja.SucursalId, caja.Id, cajero.UsuarioId, "dueno", "3333", importe, "Precio")));
        var ticket = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", Cobro(cajero, paraAutorizar with { AutorizacionId = autorizacion.Id })));

        var guardada = EnLaBase(db => db.Ventas.Include(v => v.Lineas).AsNoTracking().Single(v => v.Id == ticket.VentaId));
        var renglon = Assert.Single(guardada.Lineas);
        Assert.Equal(precio, renglon.PrecioCentavos);
        Assert.Equal(lista, renglon.PrecioOriginalCentavos);
        Assert.Equal(0, renglon.DescuentoCentavos);
        Assert.Equal(importe, guardada.TotalCentavos);
        var registros = EnLaBase(db => db.Bitacora.AsNoTracking().Where(b => b.VentaId == ticket.VentaId).ToList());
        var registro = Assert.Single(registros, b => b.Accion == AccionControlada.CambiarPrecio);
        Assert.Equal(ResultadoBitacora.Autorizado, registro.Resultado);
        Assert.DoesNotContain(registros, b => b.Accion == AccionControlada.Descuento);

        // El dueño lo sube solo, sin autorización.
        var (delDueno, _, _) = Subida(producto, 1, 500);
        var suyo = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", Cobro(dueno, delDueno)));
        Assert.Equal(ResultadoBitacora.Hecho, EnLaBase(db => db.Bitacora.AsNoTracking()
            .Single(b => b.VentaId == suyo.VentaId && b.Accion == AccionControlada.CambiarPrecio)).Resultado);
    }

    [Fact]
    public async Task Sin_conexion_el_precio_mas_alto_se_revisa_con_la_copia_y_sube_como_cambio_de_precio()
    {
        var local = servicio.Fabrica.CreateClient();
        var caja = (await local.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var interruptor = new Interruptor(servicio.Fabrica.Server.CreateHandler());
        var http = new HttpClient(interruptor);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), http, conexion);
        var cliente = new ClienteCaja(http, conexion, contingencia);
        await contingencia.LatidoAsync();
        var cajero = await cliente.EntrarAsync("cajero", "1111");
        var turno = await cliente.TurnoAsync(caja.Id) ?? await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 50000);
        var busqueda = (await cliente.BuscarAsync("pluma punto fino", caja.SucursalId)).First(r => r.Codigo is not null);
        var pluma = (await cliente.PorCodigoAsync(busqueda.Codigo!, caja.SucursalId))!;
        SolicitudCobro Cobro(LineaSolicitada linea) =>
            new(caja.Id, turno.Id, cajero.UsuarioId, [linea], [new CobroSolicitado(FormaPago.Efectivo, 1_000_000)]);

        interruptor.Caido = true;
        await contingencia.LatidoAsync();
        Assert.False(contingencia.EnLinea);

        // Sin autorización, tampoco sin conexión; con la del dueño (su PIN en la copia), se cobra.
        var (linea, precio, lista) = Subida(pluma, 2, 700);
        var requiere = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.CobrarAsync(Cobro(linea)));
        Assert.Equal("requiere-autorizacion", requiere.Codigo);
        var importe = PapeleriaPos.Nucleo.Cantidad.Piezas(2).Importe(precio);
        var autorizacion = await cliente.AutorizarAsync(new SolicitudAutorizacion(AccionControlada.CambiarPrecio, caja.SucursalId, caja.Id, cajero.UsuarioId,
            "dueno", "3333", importe, "Precio"));
        var ticket = await cliente.CobrarAsync(Cobro(linea with { AutorizacionId = autorizacion.Id }));
        Assert.Equal(importe, ticket.TotalCentavos);
        Assert.Equal(0, Assert.Single(ticket.Lineas).DescuentoCentavos);

        interruptor.Caido = false;
        await contingencia.LatidoAsync();
        Assert.Equal(0, contingencia.Pendientes);

        var subida = EnLaBase(db => db.Ventas.Include(v => v.Lineas).AsNoTracking().Single(v => v.Id == ticket.VentaId));
        Assert.Equal(lista, subida.Lineas.Single().PrecioOriginalCentavos);
        Assert.Equal(precio, subida.Lineas.Single().PrecioCentavos);
        var registros = EnLaBase(db => db.Bitacora.AsNoTracking().Where(b => b.VentaId == ticket.VentaId).ToList());
        Assert.Contains(registros, b => b.Accion == AccionControlada.CambiarPrecio && b.Resultado == ResultadoBitacora.Autorizado && b.EnContingencia);
        Assert.DoesNotContain(registros, b => b.Accion == AccionControlada.Descuento);
    }
}
