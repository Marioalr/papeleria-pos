using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>Entre la caja y el servicio de prueba: con <see cref="Caido"/>, como si la caja principal estuviera apagada.</summary>
internal sealed class Interruptor(HttpMessageHandler interno) : DelegatingHandler(interno)
{
    public bool Caido { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Caido ? throw new HttpRequestException("La caja principal no contesta.") : base.SendAsync(request, cancellationToken);
}

/// <summary>La caja de verdad (cliente, contingencia y base local) contra el servicio de verdad, apagándolo a media venta.</summary>
public sealed class ContingenciaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-local-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _baseLocal, _baseLocal + "-wal", _baseLocal + "-shm" })
            File.Delete(archivo);
    }

    private static int Folio(TicketDto t) => int.Parse(t.Folio[(t.Folio.IndexOf('-') + 1)..]);

    private static SolicitudCobro Cobro(CajaDto caja, TurnoDto turno, SesionDto cajero, ProductoVentaDto producto, int piezas, long? precio = null,
        Guid? autorizacion = null, params CobroSolicitado[] cobros) =>
        new(caja.Id, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(producto.VarianteId, producto.Presentacion.Id, piezas * 1000, producto.Descripcion, precio, autorizacion)],
            cobros.Length > 0 ? cobros : [new CobroSolicitado(FormaPago.Efectivo, 100000)]);

    [Fact]
    public async Task Sin_la_caja_principal_se_sigue_vendiendo_y_al_volver_todo_sube_una_vez()
    {
        var local = servicio.Fabrica.CreateClient();
        var caja = (await local.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var interruptor = new Interruptor(servicio.Fabrica.Server.CreateHandler());
        var http = new HttpClient(interruptor);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), http, conexion);
        var cliente = new ClienteCaja(http, conexion, contingencia);

        // En línea: baja su copia, abre turno y vende.
        await contingencia.LatidoAsync();
        Assert.NotEmpty(contingencia.Paquete!.Productos);
        var cajero = await cliente.EntrarAsync("cajero", "1111");
        var turno = await cliente.TurnoAsync(caja.Id) ?? await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 50000);
        var busqueda = (await cliente.BuscarAsync("pluma punto fino", caja.SucursalId)).First(r => r.Codigo is not null);
        var pluma = (await cliente.PorCodigoAsync(busqueda.Codigo!, caja.SucursalId))!;
        var enLinea = await cliente.CobrarAsync(Cobro(caja, turno, cajero, pluma, 1));

        // Se apaga la caja principal.
        interruptor.Caido = true;
        await contingencia.LatidoAsync();
        Assert.False(contingencia.EnLinea);

        var otraVez = await cliente.EntrarAsync("cajero", "1111");
        var sinConexion1 = await cliente.CobrarAsync(Cobro(caja, turno, cajero, (await cliente.PorCodigoAsync(busqueda.Codigo!, caja.SucursalId))!, 2));
        var sinConexion2 = await cliente.CobrarAsync(Cobro(caja, turno, cajero, pluma, 1, cobros: new CobroSolicitado(FormaPago.Transferencia, pluma.Precios[0].PrecioCentavos, "REF9")));
        var salida = await cliente.MovimientoAsync(turno.Id, new SolicitudMovimiento(TipoMovimientoCaja.Salida, 10000, "Garrafón de agua", cajero.UsuarioId));

        // Cambiar el precio sin conexión: el encargado no puede autorizarlo; el dueño sí, con su PIN en la copia local.
        var importe = 3 * 500L;
        var requiere = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.CobrarAsync(Cobro(caja, turno, cajero, pluma, 3, precio: 500)));
        await Assert.ThrowsAsync<ErrorCaja>(() => cliente.AutorizarAsync(
            new SolicitudAutorizacion(AccionControlada.CambiarPrecio, caja.SucursalId, caja.Id, cajero.UsuarioId, "encargado", "2222", importe, "Precio")));
        var autorizacion = await cliente.AutorizarAsync(
            new SolicitudAutorizacion(AccionControlada.CambiarPrecio, caja.SucursalId, caja.Id, cajero.UsuarioId, "dueno", "3333", importe, "Precio"));
        var conPrecio = await cliente.CobrarAsync(Cobro(caja, turno, cajero, pluma, 3, precio: 500, autorizacion: autorizacion.Id));

        var corteLocal = await cliente.CorteXAsync(turno.Id, cajero.UsuarioId);
        var sinOtrasCosas = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.VentasDelDiaAsync(caja.Id));

        Assert.Equal(cajero.UsuarioId, otraVez.UsuarioId);
        Assert.Equal("requiere-autorizacion", requiere.Codigo);
        Assert.Equal([Folio(enLinea) + 1, Folio(enLinea) + 2, Folio(enLinea) + 3], new[] { sinConexion1, sinConexion2, conPrecio }.Select(Folio));
        Assert.Equal(1500, conPrecio.TotalCentavos);
        Assert.True(corteLocal.EnContingencia);
        Assert.Equal("sin-conexion", sinOtrasCosas.Codigo);
        Assert.True(contingencia.Pendientes > 0);

        // Vuelve: sube la bandeja y ya trabaja en línea.
        interruptor.Caido = false;
        await contingencia.LatidoAsync();

        Assert.Equal(0, contingencia.Pendientes);
        Assert.Equal(0, contingencia.ConError);
        Assert.True(contingencia.EnLinea);
        var resumen = (await local.GetFromJsonAsync<CorteDto>($"api/turnos/{turno.Id}/resumen"))!;
        Assert.Equal(corteLocal.EsperadoCentavos, resumen.EsperadoCentavos);
        Assert.Equal(corteLocal.Ventas, resumen.Ventas);
        Assert.Equal(corteLocal.TransferenciaCentavos, resumen.TransferenciaCentavos);
        Assert.Equal(corteLocal.SalidasCentavos, resumen.SalidasCentavos);
        var delDia = (await local.GetFromJsonAsync<List<VentaDelDiaDto>>($"api/cajas/{caja.Id}/ventas-del-dia"))!;
        Assert.Contains(delDia, v => v.Folio == conPrecio.Folio && v.TotalCentavos == 1500);
        var bitacora = await (await local.PostAsJsonAsync($"api/sucursales/{caja.SucursalId}/bitacora/consulta",
            new SolicitudConsultaBitacora("encargado", "2222", DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), DateOnly.FromDateTime(DateTime.Today.AddDays(1)))))
            .Content.ReadFromJsonAsync<List<BitacoraDto>>();
        Assert.Contains(bitacora!, b => b.Accion == AccionControlada.CambiarPrecio && b.Autorizo == "Dueño de prueba" && b.Resultado == ResultadoBitacora.Autorizado);
        Assert.Contains(bitacora!, b => b.Accion == AccionControlada.CambiarPrecio && b.Resultado == ResultadoBitacora.Negado);

        // En línea, la serie sigue después de los folios que se usaron sin conexión.
        var despues = await cliente.CobrarAsync(Cobro(caja, turno, cajero, pluma, 1));
        Assert.Equal(Folio(conPrecio) + 1, Folio(despues));

        // Corte Z sin conexión: la caja cierra con su cuenta e imprime; al volver, el servicio cierra el turno igual.
        interruptor.Caido = true;
        await contingencia.LatidoAsync();
        var esperado = (await cliente.CorteXAsync(turno.Id, cajero.UsuarioId)).EsperadoCentavos;
        var corteZ = await cliente.CorteZAsync(turno.Id, new SolicitudCorteZ("encargado", "2222", new Dictionary<long, int> { [10000] = 3 }));
        Assert.Null(await cliente.TurnoAsync(caja.Id));
        interruptor.Caido = false;
        await contingencia.LatidoAsync();

        Assert.Equal(esperado, corteZ.EsperadoCentavos);
        Assert.Equal(30000 - esperado, corteZ.DiferenciaCentavos);
        Assert.Equal(0, contingencia.Pendientes);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await local.GetAsync($"api/cajas/{caja.Id}/turno")).StatusCode);
        Assert.Equal(esperado, (await local.GetFromJsonAsync<CorteDto>($"api/turnos/{turno.Id}/resumen"))!.EsperadoCentavos);
    }

    [Fact]
    public async Task Lo_que_sube_dos_veces_se_guarda_una()
    {
        var local = servicio.Fabrica.CreateClient();
        var caja = (await local.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = (await (await local.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var respuesta = await local.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == System.Net.HttpStatusCode.NoContent
            ? (await (await local.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 0))).Content.ReadFromJsonAsync<TurnoDto>())!
            : (await respuesta.Content.ReadFromJsonAsync<TurnoDto>())!;
        var paquete = (await local.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{caja.Id}/paquete"))!;
        var producto = paquete.Productos.First(p => p.Precios.Any(x => x.PresentacionId == null));
        var precio = producto.Precios.Where(x => x.PresentacionId == null).MinBy(x => x.CantidadMinimaMilesimas)!.PrecioCentavos;
        var venta = new VentaContingenciaDto(Guid.NewGuid(), caja.Id, turno.Id, cajero.UsuarioId, paquete.UltimoFolio + 1, DateTime.UtcNow,
            [new LineaContingenciaDto(producto.VarianteId, producto.Presentacion.Id, producto.Descripcion, 1000, precio)],
            [new CobroSolicitado(FormaPago.Efectivo, precio)]);

        var primera = await local.PostAsJsonAsync("api/bandeja/ventas", venta);
        var segunda = await local.PostAsJsonAsync("api/bandeja/ventas", venta);
        var mismoFolio = await local.PostAsJsonAsync("api/bandeja/ventas", venta with { Id = Guid.NewGuid() });

        Assert.True(primera.IsSuccessStatusCode, await primera.Content.ReadAsStringAsync());
        Assert.True(segunda.IsSuccessStatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, mismoFolio.StatusCode);
        var delDia = (await local.GetFromJsonAsync<List<VentaDelDiaDto>>($"api/cajas/{caja.Id}/ventas-del-dia"))!;
        Assert.Single(delDia, v => v.Id == venta.Id);
    }
}
