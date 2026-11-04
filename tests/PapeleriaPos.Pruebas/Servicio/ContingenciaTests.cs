using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Entre la caja y el servicio de prueba: con <see cref="Caido"/>, como si la caja principal estuviera apagada; con
/// <see cref="Rechaza"/>, contesta con un error a lo que cumpla, como si el servicio no lo hubiera podido guardar; con
/// <see cref="PierdeRespuesta"/>, lo manda (el servicio lo guarda) pero la respuesta no llega; con
/// <see cref="TardaDemasiado"/>, lo manda (el servicio lo guarda) pero la respuesta llega después del tope del cliente.
/// </summary>
internal sealed class Interruptor(HttpMessageHandler interno) : DelegatingHandler(interno)
{
    public bool Caido { get; set; }
    public Func<HttpRequestMessage, bool>? Rechaza { get; set; }
    public Func<HttpRequestMessage, bool>? PierdeRespuesta { get; set; }
    public Func<HttpRequestMessage, bool>? TardaDemasiado { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Caido)
            throw new HttpRequestException(HttpRequestError.ConnectionError, "La caja principal no contesta.");
        if (Rechaza?.Invoke(request) == true)
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new ErrorDto("La base estaba ocupada.")) };
        var respuesta = await base.SendAsync(request, cancellationToken);
        if (TardaDemasiado?.Invoke(request) == true)
        {
            respuesta.Dispose();
            throw new TaskCanceledException("Pasó el tope del cliente.");
        }
        if (PierdeRespuesta?.Invoke(request) != true)
            return respuesta;
        respuesta.Dispose();
        throw new HttpRequestException("Se cortó antes de la respuesta.");
    }
}

/// <summary>La caja de verdad (cliente, contingencia y base local) contra el servicio de verdad, apagándolo a media venta.</summary>
public sealed class ContingenciaTests(ServicioDePrueba servicio) : IClassFixture<ServicioDePrueba>, IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-local-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        ArchivosDePrueba.BorrarBase(_baseLocal);
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

        // Vuelve: sube la bandeja y ya trabaja en línea. Latidos hasta que contesta y no queda nada por subir, como los repite la
        // caja: al volver, el primero prueba antes quién es la caja principal, y en una máquina ocupada (el CI de Windows) puede
        // pasar de su plazo y terminar sin subir nada (pasó una vez: quedaban 6 por subir).
        interruptor.Caido = false;
        for (var latido = 1; !(contingencia.EnLinea && contingencia.Pendientes == 0); latido++)
        {
            Assert.True(latido <= 10, $"En 10 latidos no subió todo: quedan {contingencia.Pendientes}.");
            await contingencia.LatidoAsync();
        }

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
        var quienConsulta = (await (await local.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var bitacora = await local.GetFromJsonAsync<List<BitacoraDto>>($"api/cajas/{caja.Id}/bitacora?usuarioId={quienConsulta.UsuarioId}"
            + $"&desde={DateTime.Today.AddDays(-1):yyyy-MM-dd}&hasta={DateTime.Today.AddDays(1):yyyy-MM-dd}");
        Assert.Contains(bitacora!, b => b.Accion == AccionControlada.CambiarPrecio && b.Autorizo == "Dueño de prueba" && b.Resultado == ResultadoBitacora.Autorizado);
        Assert.Contains(bitacora!, b => b.Accion == AccionControlada.CambiarPrecio && b.Resultado == ResultadoBitacora.Negado);

        // En línea, la serie sigue después de los folios que se usaron sin conexión.
        var despues = await cliente.CobrarAsync(Cobro(caja, turno, cajero, pluma, 1));
        Assert.Equal(Folio(conPrecio) + 1, Folio(despues));

        // Corte Z sin conexión: la caja cierra con su cuenta e imprime; al volver, el servicio cierra el turno igual.
        interruptor.Caido = true;
        await contingencia.LatidoAsync();
        var esperado = (await cliente.CorteXAsync(turno.Id, cajero.UsuarioId)).EsperadoCentavos;
        // El encargado entra con su PIN (con la copia local) y escribe el total; no se lo vuelve a pedir.
        var encargado = await cliente.EntrarAsync("encargado", "2222");
        var corteZ = await cliente.CorteZAsync(turno.Id, new SolicitudCorteZ(encargado.UsuarioId, 30000));
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
    public async Task Lo_cobrado_sin_conexion_que_llega_despues_del_corte_Z_se_guarda_con_su_turno_fuera_de_ese_corte()
    {
        var local = servicio.Fabrica.CreateClient();
        var caja = (await local.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var dueno = (await (await local.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333"))).Content.ReadFromJsonAsync<SesionDto>())!;
        using (var alcance = servicio.Fabrica.Services.CreateScope())
        {
            var db = alcance.ServiceProvider.GetRequiredService<PosDbContext>();
            db.ChatsTelegram.Add(new ChatTelegram { UsuarioId = dueno.UsuarioId, ChatId = 9393, VinculadoUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
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

        // Sin conexión: una venta en efectivo, una salida (F8) y el corte Z con lo que se contó.
        interruptor.Caido = true;
        await contingencia.LatidoAsync();
        var venta = await cliente.CobrarAsync(Cobro(caja, turno, cajero, pluma, 2));
        await cliente.MovimientoAsync(turno.Id, new SolicitudMovimiento(TipoMovimientoCaja.Salida, 10000, "Pago al de la basura", cajero.UsuarioId));
        var encargado = await cliente.EntrarAsync("encargado", "2222");
        var corteLocal = await cliente.CorteZAsync(turno.Id, new SolicitudCorteZ(encargado.UsuarioId, 30000));

        // Vuelve, pero la venta y la salida no se pudieron guardar esa vez; el corte Z sí sube y cierra el turno sin ellas.
        interruptor.Rechaza = r => r.RequestUri!.AbsolutePath == "/api/bandeja/ventas" || r.RequestUri.AbsolutePath.EndsWith("/movimientos");
        interruptor.Caido = false;
        await contingencia.LatidoAsync();
        Assert.Equal(2, contingencia.ConError);
        using var alcance1 = servicio.Fabrica.Services.CreateScope();
        var antes = await alcance1.ServiceProvider.GetRequiredService<PosDbContext>().Cortes.AsNoTracking()
            .SingleAsync(c => c.TurnoId == turno.Id && c.Tipo == TipoCorte.Z);
        Assert.Equal(venta.TotalCobradoCentavos - 10000, corteLocal.EsperadoCentavos - antes.EsperadoCentavos);

        // El reintento de lo que falló: ya está cobrado, así que entra aunque su turno ya tenga corte.
        interruptor.Rechaza = null;
        await contingencia.SubirAsync();

        Assert.Equal(0, contingencia.ConError);
        Assert.Equal(0, contingencia.Pendientes);
        using var alcance2 = servicio.Fabrica.Services.CreateScope();
        var db2 = alcance2.ServiceProvider.GetRequiredService<PosDbContext>();
        var subida = await db2.Ventas.AsNoTracking().SingleAsync(v => v.Id == venta.VentaId);
        var salida = await db2.MovimientosCaja.AsNoTracking().SingleAsync(m => m.TurnoId == turno.Id && m.Motivo == "Pago al de la basura");
        Assert.Equal(turno.Id, subida.TurnoId);
        Assert.Equal(venta.Folio, subida.FolioTexto);
        Assert.True(subida.EnContingencia);
        Assert.NotNull(subida.FueraDeCorteUtc);
        Assert.NotNull(salida.FueraDeCorteUtc);

        // El corte cerrado no cambia.
        var despues = await db2.Cortes.AsNoTracking().SingleAsync(c => c.TurnoId == turno.Id && c.Tipo == TipoCorte.Z);
        Assert.Equal((antes.Ventas, antes.EfectivoVentasCentavos, antes.SalidasCentavos, antes.EsperadoCentavos, antes.DiferenciaCentavos),
            (despues.Ventas, despues.EfectivoVentasCentavos, despues.SalidasCentavos, despues.EsperadoCentavos, despues.DiferenciaCentavos));

        // Al dueño le llega un aviso por cada una, y el resumen del día las menciona.
        var avisos = await db2.MensajesTelegram.AsNoTracking().Where(m => m.ChatId == 9393 && m.Tipo == TipoAlerta.FueraDeCorte).Select(m => m.Texto).ToListAsync();
        Assert.Equal(2, avisos.Count);
        Assert.Contains(avisos, a => a.Contains($"La venta {venta.Folio}") && a.Contains("sobrante"));
        Assert.Contains(avisos, a => a.Contains("La salida de efectivo") && a.Contains("Pago al de la basura") && a.Contains("faltante"));
        var resumen = await AlertasTelegram.ResumenAsync(db2, caja.SucursalId, DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow)));
        Assert.Contains($"Llegaron después de su corte: 1 venta · {AlertasTelegram.Dinero(venta.TotalCobradoCentavos)}; 1 salida · $100.00", resumen);
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
        var delDia = (await local.GetFromJsonAsync<List<VentaDelDiaDto>>($"api/cajas/{caja.Id}/ventas-del-dia"))!;
        Assert.Single(delDia, v => v.Id == venta.Id);
        // Otra venta con ese folio (se fue la luz antes de que la caja lo anotara): ya se cobró, no se rechaza. Toma el siguiente y su
        // nota dice con cuál salió el ticket.
        Assert.True(mismoFolio.IsSuccessStatusCode, await mismoFolio.Content.ReadAsStringAsync());
        await using var db = servicio.Base();
        var conOtroFolio = await db.Ventas.AsNoTracking().SingleAsync(v => v.TurnoId == turno.Id && v.Id != venta.Id && v.Folio == venta.Folio + 1);
        Assert.Contains($"-{venta.Folio:000000}", conOtroFolio.Nota);
    }

    /// <summary>Caja, cajero, turno y su copia, directo contra el servicio: lo que sube la bandeja, sin pasar por el cobro de la caja.</summary>
    private async Task<(HttpClient Local, CajaDto Caja, SesionDto Cajero, TurnoDto Turno, PaqueteContingenciaDto Paquete)> ParaSubirAsync()
    {
        var local = servicio.Fabrica.CreateClient();
        var caja = (await local.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = (await (await local.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var respuesta = await local.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == HttpStatusCode.NoContent
            ? (await (await local.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 0))).Content.ReadFromJsonAsync<TurnoDto>())!
            : (await respuesta.Content.ReadFromJsonAsync<TurnoDto>())!;
        return (local, caja, cajero, turno, (await local.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{caja.Id}/paquete"))!);
    }

    /// <summary>
    /// «Lo que ya se cobró nunca se rechaza por llegar tarde»: un producto que se dio de baja mientras la venta esperaba subir no
    /// la deja afuera. Entra con ese mismo producto, a lo que se cobró, y va a eleventa; y reintentarla la deja en una.
    /// </summary>
    [Fact]
    public async Task La_venta_de_un_producto_dado_de_baja_mientras_tanto_sube_como_se_cobro()
    {
        var (local, caja, cajero, turno, paquete) = await ParaSubirAsync();
        var producto = paquete.Productos.Last(p => p.LlevaInventario && p.Precios.Any(x => x.PresentacionId == null));
        var precio = producto.Precios.Where(x => x.PresentacionId == null).MinBy(x => x.CantidadMinimaMilesimas)!.PrecioCentavos;
        var venta = new VentaContingenciaDto(Guid.NewGuid(), caja.Id, turno.Id, cajero.UsuarioId, paquete.UltimoFolio + 1, DateTime.UtcNow,
            [new LineaContingenciaDto(producto.VarianteId, producto.Presentacion.Id, producto.Descripcion, 2000, precio)],
            [new CobroSolicitado(FormaPago.Efectivo, 2 * precio)]);
        await CambiarVarianteAsync(producto.VarianteId, activa: false);
        try
        {
            var subio = await local.PostAsJsonAsync("api/bandeja/ventas", venta);
            var otraVez = await local.PostAsJsonAsync("api/bandeja/ventas", venta);

            Assert.True(subio.IsSuccessStatusCode, await subio.Content.ReadAsStringAsync());
            Assert.True(otraVez.IsSuccessStatusCode, await otraVez.Content.ReadAsStringAsync());
            await using var db = servicio.Base();
            var guardada = await db.Ventas.AsNoTracking().Include(v => v.Lineas).SingleAsync(v => v.Id == venta.Id);
            var linea = Assert.Single(guardada.Lineas);
            Assert.Equal(producto.VarianteId, linea.VarianteId);
            Assert.Equal(producto.Descripcion, linea.Descripcion);
            Assert.Equal(2 * precio, guardada.TotalCentavos);
            Assert.Equal(EstadoVenta.Pagada, guardada.Estado);
        }
        finally
        {
            await CambiarVarianteAsync(producto.VarianteId, activa: true);
        }
    }

    /// <summary>
    /// Lo mismo cuando ya no se puede armar con el producto (se quitó su presentación, o una fracción de lo que ahora se vende por
    /// pieza): entra como artículo común con su nombre y lo que se cobró, con la promoción como rebaja; y reintentarla la deja en una.
    /// </summary>
    [Fact]
    public async Task La_venta_que_ya_no_se_puede_armar_con_su_producto_sube_como_articulo_comun()
    {
        var (local, caja, cajero, turno, paquete) = await ParaSubirAsync();
        var producto = paquete.Productos.First(p => p.Unidad == UnidadBase.Pieza && p.Precios.Any(x => x.PresentacionId == null));
        var precio = producto.Precios.Where(x => x.PresentacionId == null).MinBy(x => x.CantidadMinimaMilesimas)!.PrecioCentavos;
        var presentacionQuitada = new LineaContingenciaDto(producto.VarianteId, Guid.NewGuid(), "Caja de plumas", 2000, 5000,
            PromocionId: Guid.NewGuid(), Promocion: "2x1", AhorroPromocionCentavos: 1500);
        var fraccion = new LineaContingenciaDto(producto.VarianteId, producto.Presentacion.Id, producto.Descripcion, 500, 2000);
        var venta = new VentaContingenciaDto(Guid.NewGuid(), caja.Id, turno.Id, cajero.UsuarioId, paquete.UltimoFolio + 1, DateTime.UtcNow,
            [presentacionQuitada, fraccion], [new CobroSolicitado(FormaPago.Efectivo, 9500)]);

        var subio = await local.PostAsJsonAsync("api/bandeja/ventas", venta);
        var otraVez = await local.PostAsJsonAsync("api/bandeja/ventas", venta);

        Assert.True(subio.IsSuccessStatusCode, await subio.Content.ReadAsStringAsync());
        Assert.True(otraVez.IsSuccessStatusCode, await otraVez.Content.ReadAsStringAsync());
        await using var db = servicio.Base();
        var guardada = await db.Ventas.AsNoTracking().Include(v => v.Lineas).SingleAsync(v => v.Id == venta.Id);
        Assert.Equal(2, guardada.Lineas.Count);
        Assert.All(guardada.Lineas, l => Assert.Null(l.VarianteId));
        Assert.Equal(8500, guardada.Lineas.Single(l => l.Descripcion == "Caja de plumas").ImporteCentavos);
        Assert.Equal(1000, guardada.Lineas.Single(l => l.Descripcion == producto.Descripcion).ImporteCentavos);
        Assert.Equal(9500, guardada.TotalCentavos);
    }

    private async Task CambiarVarianteAsync(Guid varianteId, bool activa)
    {
        await using var db = servicio.Base();
        (await db.Variantes.SingleAsync(v => v.Id == varianteId)).Activa = activa;
        await db.SaveChangesAsync();
        // Hasta que el catálogo del servicio se entera, como después de una lectura de eleventa.
        var local = servicio.Fabrica.CreateClient();
        var caja = (await local.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        for (var i = 0; i < 100 && (await local.GetAsync($"api/productos/{varianteId}?sucursalId={caja.SucursalId}")).IsSuccessStatusCode != activa; i++)
            await Task.Delay(50);
    }

    /// <summary>
    /// Una venta que sube con el Id de una que ya está, pero con otras líneas (la caja la cobró otra vez sin conexión, cambiada,
    /// sin saber que la primera sí había quedado; entrega 5, parte 2, revisión): no se guarda otra ni se descarta callada; queda
    /// por revisar, con los dos folios. La misma, igual, sigue entrando una vez.
    /// </summary>
    [Fact]
    public async Task Lo_que_sube_con_el_Id_de_una_venta_que_ya_esta_pero_distinta_queda_por_revisar()
    {
        var local = servicio.Fabrica.CreateClient();
        var caja = (await local.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = (await (await local.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111"))).Content.ReadFromJsonAsync<SesionDto>())!;
        var respuesta = await local.GetAsync($"api/cajas/{caja.Id}/turno");
        var turno = respuesta.StatusCode == System.Net.HttpStatusCode.NoContent
            ? (await (await local.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 0))).Content.ReadFromJsonAsync<TurnoDto>())!
            : (await respuesta.Content.ReadFromJsonAsync<TurnoDto>())!;
        var paquete = (await local.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{caja.Id}/paquete"))!;
        var producto = paquete.Productos.Where(p => p.Precios.Any(x => x.PresentacionId == null)).Skip(1).First();
        var precio = producto.Precios.Where(x => x.PresentacionId == null).MinBy(x => x.CantidadMinimaMilesimas)!.PrecioCentavos;
        var linea = new LineaContingenciaDto(producto.VarianteId, producto.Presentacion.Id, producto.Descripcion, 1000, precio);
        var venta = new VentaContingenciaDto(Guid.NewGuid(), caja.Id, turno.Id, cajero.UsuarioId, paquete.UltimoFolio + 1, DateTime.UtcNow,
            [linea], [new CobroSolicitado(FormaPago.Efectivo, precio)]);

        var primera = await local.PostAsJsonAsync("api/bandeja/ventas", venta);
        var distinta = await local.PostAsJsonAsync("api/bandeja/ventas", venta with
        {
            Folio = venta.Folio + 1, Lineas = [linea with { CantidadMilesimas = 3000 }], Cobros = [new CobroSolicitado(FormaPago.Efectivo, 3 * precio)],
        });
        var igual = await local.PostAsJsonAsync("api/bandeja/ventas", venta);

        Assert.True(primera.IsSuccessStatusCode, await primera.Content.ReadAsStringAsync());
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, distinta.StatusCode);
        var porRevisar = (await distinta.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje;
        Assert.Contains("hay que revisarla", porRevisar);
        Assert.Contains($"{caja.Serie}-{venta.Folio:000000}", porRevisar);
        Assert.Contains($"{caja.Serie}-{venta.Folio + 1:000000}", porRevisar);
        Assert.True(igual.IsSuccessStatusCode);
        await using var db = servicio.Base();
        var guardada = await db.Ventas.AsNoTracking().Include(x => x.Lineas).SingleAsync(x => x.Id == venta.Id);
        Assert.Equal(1000, Assert.Single(guardada.Lineas).Cantidad.Milesimas);
    }
}
