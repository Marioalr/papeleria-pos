using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>El redondeo al peso de punta a punta: en línea, al cancelar y sin conexión (fase 3, entrega 1).</summary>
public sealed class RedondeoApiTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-redondeo-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _baseLocal, _baseLocal + "-wal", _baseLocal + "-shm" })
            File.Delete(archivo);
    }

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>Un producto por pieza, con código, cuyo menudeo trae 50 centavos ($12.50): en efectivo se cobra $13.</summary>
    private async Task<(string Codigo, long Precio)> ProductoConCincuentaCentavosAsync()
    {
        using var alcance = servicio.Fabrica.Services.CreateScope();
        var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
        var candidatos = await (from p in db.Precios
                                join pr in db.Presentaciones on p.VarianteId equals pr.VarianteId
                                join c in db.CodigosBarras on pr.Id equals c.PresentacionId
                                join v in db.Variantes on p.VarianteId equals v.Id
                                where p.PresentacionId == null && p.SucursalId == null && pr.EsBase && v.Activa && v.UnidadBase == UnidadBase.Pieza
                                select new { c.Codigo, p.VarianteId, p.CantidadMinima, p.PrecioCentavos }).ToListAsync();
        return candidatos.GroupBy(x => x.VarianteId)
            .Select(g => g.OrderBy(x => x.CantidadMinima.Milesimas).First())
            .Where(x => x.PrecioCentavos % 100 == 50)
            .Select(x => (x.Codigo, x.PrecioCentavos))
            .First();
    }

    [Fact]
    public async Task En_linea_se_cobra_al_peso_y_al_cancelar_se_regresa_lo_cobrado()
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var respuesta = await http.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == HttpStatusCode.NoContent
            ? await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)))
            : await Leer<TurnoDto>(respuesta);
        var (codigo, precio) = await ProductoConCincuentaCentavosAsync();
        var producto = await Leer<ProductoVentaDto>(await http.GetAsync($"api/productos/codigo/{codigo}?sucursalId={caja.SucursalId}"));
        var antes = await Leer<CorteDto>(await http.GetAsync($"api/turnos/{turno.Id}/resumen"));
        SolicitudCobro Cobro(long efectivo) => new(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 1000, producto.Descripcion)], [new CobroSolicitado(FormaPago.Efectivo, efectivo)]);

        var exacto = await http.PostAsJsonAsync("api/ventas", Cobro(precio));
        var ticket = await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", Cobro(precio + 50)));
        var conVenta = await Leer<CorteDto>(await http.GetAsync($"api/turnos/{turno.Id}/resumen"));
        var cancelacion = await Leer<ComprobanteDevolucionDto>(await http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/cancelacion",
            new SolicitudCancelacion(encargado.UsuarioId, caja.Id, "Se arrepintió", null)));
        var despues = await Leer<CorteDto>(await http.GetAsync($"api/turnos/{turno.Id}/resumen"));

        Assert.Equal(HttpStatusCode.BadRequest, exacto.StatusCode);
        Assert.Contains("0.50", (await exacto.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
        Assert.Equal((precio, 50L, precio + 50, 0L), (ticket.TotalCentavos, ticket.RedondeoCentavos, ticket.TotalCobradoCentavos, ticket.CambioCentavos));
        Assert.Equal(precio + 50, ticket.Cobros.Single().MontoCentavos);
        Assert.Equal((antes.RedondeoCentavos + 50, antes.EsperadoCentavos + precio + 50), (conVenta.RedondeoCentavos, conVenta.EsperadoCentavos));
        // Se regresa lo que se cobró ($13), no el total ($12.50): el esperado vuelve a donde estaba y el redondeo se anula.
        Assert.Equal((precio + 50, 50L, 0L), (cancelacion.ReembolsoEfectivoCentavos, cancelacion.RedondeoCentavos, cancelacion.CobradoSinEfectivoCentavos));
        Assert.Equal((antes.RedondeoCentavos, antes.EsperadoCentavos), (despues.RedondeoCentavos, despues.EsperadoCentavos));

        using var alcance = servicio.Fabrica.Services.CreateScope();
        var venta = await alcance.ServiceProvider.GetRequiredService<PosDbContext>().Ventas.AsNoTracking().SingleAsync(v => v.Id == ticket.VentaId);
        Assert.Equal((precio, 50L), (venta.TotalCentavos, venta.RedondeoCentavos));
    }

    [Fact]
    public async Task Sin_conexion_se_redondea_igual_y_el_corte_cuadra_al_subir()
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
        var (codigo, precio) = await ProductoConCincuentaCentavosAsync();
        var producto = (await cliente.PorCodigoAsync(codigo, caja.SucursalId))!;

        interruptor.Caido = true;
        await contingencia.LatidoAsync();
        var ticket = await cliente.CobrarAsync(new SolicitudCobro(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, 1000, producto.Descripcion)], [new CobroSolicitado(FormaPago.Efectivo, 20000)]));
        var corteLocal = await cliente.CorteXAsync(turno.Id, cajero.UsuarioId);

        interruptor.Caido = false;
        await contingencia.LatidoAsync();
        var resumen = (await local.GetFromJsonAsync<CorteDto>($"api/turnos/{turno.Id}/resumen"))!;

        Assert.Equal((50L, 20000 - precio - 50), (ticket.RedondeoCentavos, ticket.CambioCentavos));
        Assert.True(corteLocal.EnContingencia);
        Assert.Equal((0, 0), (contingencia.Pendientes, contingencia.ConError));
        Assert.Equal((corteLocal.EsperadoCentavos, corteLocal.RedondeoCentavos), (resumen.EsperadoCentavos, resumen.RedondeoCentavos));
        using var alcance = servicio.Fabrica.Services.CreateScope();
        var venta = await alcance.ServiceProvider.GetRequiredService<PosDbContext>().Ventas.AsNoTracking().Include(v => v.Cobros)
            .SingleAsync(v => v.Id == ticket.VentaId);
        Assert.Equal((50L, precio + 50), (venta.RedondeoCentavos, venta.Cobros.Single().MontoCentavos));
    }
}
