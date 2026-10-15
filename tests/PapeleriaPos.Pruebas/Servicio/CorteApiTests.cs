using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Datos;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Servicio.Telegram;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El corte (entrega 6, docs/propuesta-interfaz.md «Corte», 2026-10-14): el del turno lo hace quien ya entró con su PIN y
/// tiene el permiso, con solo el total y a ciegas; el del día junta todas las cajas y turnos, y se cierra sin turnos
/// abiertos ni nada por subir de otra caja. Cada prueba con su propia base: los turnos se cierran.
/// </summary>
public sealed class CorteApiTests : IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-local-{Guid.NewGuid():N}.db");

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

    private static async Task<ErrorDto> RechazoAsync(HttpResponseMessage r, HttpStatusCode codigo = HttpStatusCode.BadRequest)
    {
        Assert.Equal(codigo, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!;
    }

    private sealed record Puesto(HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno);

    private static async Task<Puesto> PuestoAsync(ServicioDePrueba servicio)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        async Task<SesionDto> Entrar(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        return new Puesto(http, caja, await Entrar("cajero", "1111"), await Entrar("encargado", "2222"), await Entrar("dueno", "3333"));
    }

    private static async Task<TurnoDto> AbrirAsync(HttpClient http, Guid cajaId, SesionDto cajero, long fondo) =>
        await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(cajaId, cajero.UsuarioId, fondo)));

    /// <summary>Un artículo común por <paramref name="precio"/>, pagado como se diga.</summary>
    private static async Task<TicketDto> VenderAsync(HttpClient http, Guid cajaId, TurnoDto turno, SesionDto cajero, long precio, CobroSolicitado cobro) =>
        await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(cajaId, turno.Id, cajero.UsuarioId,
            [new LineaSolicitada(null, null, 1000, "Recuerdo", precio)], [cobro])));

    private static async Task MovimientoAsync(HttpClient http, TurnoDto turno, SesionDto quien, TipoMovimientoCaja tipo, long monto, string motivo) =>
        await Leer<ComprobanteMovimientoDto>(await http.PostAsJsonAsync($"api/turnos/{turno.Id}/movimientos", new SolicitudMovimiento(tipo, monto, motivo, quien.UsuarioId)));

    private static Task<HttpResponseMessage> CortarAsync(HttpClient http, TurnoDto turno, SesionDto quien, long contado) =>
        http.PostAsJsonAsync($"api/turnos/{turno.Id}/corte-z", new SolicitudCorteZ(quien.UsuarioId, contado));

    [Fact]
    public async Task El_corte_del_turno_lo_hace_quien_entro_con_el_permiso_solo_con_el_total_y_a_ciegas()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var p = await PuestoAsync(servicio);
        var turno = await AbrirAsync(p.Http, p.Caja.Id, p.Cajero, 30000);
        await MovimientoAsync(p.Http, turno, p.Cajero, TipoMovimientoCaja.Salida, 5000, "Garrafón");
        await VenderAsync(p.Http, p.Caja.Id, turno, p.Cajero, 1000, new CobroSolicitado(FormaPago.Tarjeta, 1000, "Aut 1"));

        var crudo = await p.Http.GetStringAsync($"api/turnos/{turno.Id}/corte-informativo?usuarioId={p.Cajero.UsuarioId}");
        var delCajero = await p.Http.GetFromJsonAsync<CorteInformativoDto>($"api/turnos/{turno.Id}/corte-informativo?usuarioId={p.Cajero.UsuarioId}");
        var delEncargado = await p.Http.GetFromJsonAsync<CorteInformativoDto>($"api/turnos/{turno.Id}/corte-informativo?usuarioId={p.Encargado.UsuarioId}");
        var delDueno = await p.Http.GetFromJsonAsync<CorteInformativoDto>($"api/turnos/{turno.Id}/corte-informativo?usuarioId={p.Dueno.UsuarioId}");
        // El cajero ya entró con su PIN, pero no tiene el permiso: el servicio lo revisa aunque la caja no se lo ofrezca.
        var sinPermiso = await RechazoAsync(await CortarAsync(p.Http, turno, p.Cajero, 25000), HttpStatusCode.Forbidden);
        var corte = await Leer<CorteDto>(await CortarAsync(p.Http, turno, p.Encargado, 20000));

        // Antes de cerrar no viaja el efectivo: ni lo esperado, ni el fondo, ni las entradas y salidas.
        foreach (var dato in new[] { "esperado", "efectivo", "fondo", "salidas", "entradas", "contado" })
            Assert.DoesNotContain(dato, crudo, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((1, 1000L, 0L), (delCajero!.Ventas, delCajero.TarjetaCentavos, delCajero.TransferenciaCentavos));
        Assert.False(delCajero.PuedeCortar);
        Assert.Equal(["Encargado de prueba", "Dueño de prueba"], delCajero.QuienesCortan.Select(q => q.Nombre));
        Assert.Equal((true, false), (delEncargado!.PuedeCortar, delEncargado.VeElDia));
        Assert.Equal((true, true), (delDueno!.PuedeCortar, delDueno.VeElDia));
        Assert.Equal("no-permitido", sinPermiso.Codigo);
        Assert.Equal("El corte del turno lo hace quien tiene ese permiso: Encargado de prueba o Dueño de prueba.", sinPermiso.Mensaje);
        // Al cerrar: lo esperado, lo contado y la diferencia, a nombre del cajero del turno. Sin Telegram vinculado, no avisa.
        Assert.Equal(("Cajero de prueba", "Encargado de prueba"), (corte.Cajero, corte.RealizadoPor));
        Assert.Equal((25000L, 20000L, -5000L), (corte.EsperadoCentavos, corte.ContadoCentavos!.Value, corte.DiferenciaCentavos!.Value));
        Assert.Null(corte.UmbralAvisoCentavos);
        Assert.Equal(HttpStatusCode.NoContent, (await p.Http.GetAsync($"api/cajas/{p.Caja.Id}/turno")).StatusCode);
        await using var db = servicio.Base();
        Assert.Equal(0, await db.CortesConteo.CountAsync());
        var bitacora = await db.Bitacora.AsNoTracking().Where(b => b.Accion == AccionControlada.CorteZ).OrderBy(b => b.FechaUtc).ToListAsync();
        Assert.Equal([(p.Cajero.UsuarioId, ResultadoBitacora.Negado), (p.Encargado.UsuarioId, ResultadoBitacora.Hecho)],
            bitacora.Select(b => (b.UsuarioId!.Value, b.Resultado)));
        Assert.Equal(-5000, bitacora[1].MontoCentavos);
    }

    [Fact]
    public async Task El_corte_sin_conexion_sube_con_el_total_y_el_conteo_de_una_caja_de_antes_tambien_entra()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var p = await PuestoAsync(servicio);
        var primero = await AbrirAsync(p.Http, p.Caja.Id, p.Cajero, 10000);
        var nuevo = new CorteContingenciaDto(Ids.Nuevo(), p.Encargado.UsuarioId, null, DateTime.UtcNow, 12000);

        var conTotal = await Leer<CorteDto>(await p.Http.PostAsJsonAsync($"api/bandeja/turnos/{primero.Id}/corte-z", nuevo));
        // La respuesta se perdió y la caja lo vuelve a subir: es el mismo corte.
        var otraVez = await Leer<CorteDto>(await p.Http.PostAsJsonAsync($"api/bandeja/turnos/{primero.Id}/corte-z", nuevo));
        // Una caja de antes del 2026-10-14 sube el conteo billete por billete, con el JSON de entonces: no se atora.
        var segundo = await AbrirAsync(p.Http, p.Caja.Id, p.Cajero, 10000);
        var deAntes = $$"""{"id":"{{Ids.Nuevo()}}","encargadoId":"{{p.Encargado.UsuarioId}}","conteo":{"5000":1,"1000":3},"fechaUtc":"{{DateTime.UtcNow:O}}"}""";
        var conConteo = await Leer<CorteDto>(await p.Http.PostAsync($"api/bandeja/turnos/{segundo.Id}/corte-z",
            new StringContent(deAntes, Encoding.UTF8, "application/json")));

        Assert.Equal((12000L, 2000L, true), (conTotal.ContadoCentavos!.Value, conTotal.DiferenciaCentavos!.Value, conTotal.EnContingencia));
        Assert.Equal("Encargado de prueba", conTotal.RealizadoPor);
        Assert.Equal(conTotal.ContadoCentavos, otraVez.ContadoCentavos);
        Assert.Equal((8000L, -2000L), (conConteo.ContadoCentavos!.Value, conConteo.DiferenciaCentavos!.Value));
        Assert.Equal(HttpStatusCode.NoContent, (await p.Http.GetAsync($"api/cajas/{p.Caja.Id}/turno")).StatusCode);
        await using var db = servicio.Base();
        Assert.Equal(2, await db.Cortes.CountAsync(c => c.Tipo == TipoCorte.Z));
        Assert.Equal(0, await db.CortesConteo.CountAsync());
        Assert.Equal(2, await db.Bitacora.CountAsync(b => b.Accion == AccionControlada.CorteZ && b.Detalle.StartsWith("Corte del turno de Cajero de prueba sin conexión")));
    }

    [Fact]
    public async Task Sin_conexion_el_corte_tambien_es_de_quien_entro_con_el_permiso_y_al_volver_sube()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var latidos = new Anotador(servicio.Fabrica.Server.CreateHandler());
        var interruptor = new Interruptor(latidos);
        var http = new HttpClient(interruptor);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), http, conexion);
        var cliente = new ClienteCaja(http, conexion, contingencia);
        await contingencia.LatidoAsync();
        var cajero = await cliente.EntrarAsync("cajero", "1111");
        var turno = await cliente.AbrirTurnoAsync(caja.Id, cajero.UsuarioId, 20000);
        interruptor.Caido = true;
        await contingencia.LatidoAsync();

        var delCajero = await cliente.CorteInformativoAsync(turno.Id, cajero.UsuarioId);
        var negado = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.CorteZAsync(turno.Id, new SolicitudCorteZ(cajero.UsuarioId, 20000)));
        var encargado = await cliente.EntrarAsync("encargado", "2222");
        var delEncargado = await cliente.CorteInformativoAsync(turno.Id, encargado.UsuarioId);
        var corte = await cliente.CorteZAsync(turno.Id, new SolicitudCorteZ(encargado.UsuarioId, 19000));
        var delDia = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.CorteDelDiaAsync(caja.Id, encargado.UsuarioId));

        Assert.True(delCajero.EnContingencia);
        Assert.Null(delCajero.TransferenciasPorVerificar);
        Assert.False(delCajero.PuedeCortar);
        Assert.Equal(["Encargado de prueba", "Dueño de prueba"], delCajero.QuienesCortan.Select(q => q.Nombre));
        Assert.Equal("no-permitido", negado.Codigo);
        Assert.True(delEncargado.PuedeCortar);
        Assert.Equal((true, 19000L, -1000L, "Encargado de prueba"), (corte.EnContingencia, corte.ContadoCentavos!.Value, corte.DiferenciaCentavos!.Value, corte.RealizadoPor));
        Assert.Equal(ClienteCaja.CorteDelDiaSinConexion, delDia.Message);
        Assert.Null(await cliente.TurnoAsync(caja.Id));

        // Vuelve: en su latido dice lo que tiene por subir (el intento negado y el corte), lo sube y en seguida dice que ya no,
        // sin esperar al siguiente latido (si la apagan en esos segundos, el corte del día no se queda esperando).
        interruptor.Caido = false;
        latidos.Rutas.Clear();
        await contingencia.LatidoAsync();
        var presencia = servicio.Fabrica.Services.GetRequiredService<PresenciaCajas>();
        Assert.Equal([$"/api/estado?caja={caja.Id}&porSubir=2&conError=0", $"/api/estado?caja={caja.Id}&porSubir=0&conError=0"],
            latidos.Rutas.Where(r => r.StartsWith("/api/estado")));
        Assert.Equal(0, contingencia.Pendientes);
        Assert.Equal(0, presencia.Foto()[caja.Id].PorSubir);

        await using var db = servicio.Base();
        var subido = await db.Cortes.AsNoTracking().SingleAsync(c => c.TurnoId == turno.Id && c.Tipo == TipoCorte.Z);
        Assert.Equal((19000L, -1000L, encargado.UsuarioId), (subido.ContadoCentavos!.Value, subido.DiferenciaCentavos!.Value, subido.RealizadoPorId));
        Assert.True(await db.Bitacora.AnyAsync(b => b.Accion == AccionControlada.CorteZ && b.Resultado == ResultadoBitacora.Negado
            && b.UsuarioId == cajero.UsuarioId && b.EnContingencia));
    }

    [Fact]
    public async Task El_corte_del_dia_junta_las_cajas_y_se_cierra_sin_turnos_abiertos_ni_nada_por_subir()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var p = await PuestoAsync(servicio);
        var alta = await Leer<AltaCajaDto>(await p.Http.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", p.Caja.SucursalId, null, "Caja 2", "B", "PC-2")));
        var caja2 = servicio.Fabrica.CreateClient();
        caja2.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);
        string Ruta(Guid usuario) => $"api/cajas/{p.Caja.Id}/corte-del-dia?usuarioId={usuario}";
        Task<HttpResponseMessage> Cerrar() => p.Http.PostAsJsonAsync($"api/cajas/{p.Caja.Id}/corte-del-dia/cierre", new SolicitudCierreDia(p.Dueno.UsuarioId));

        // Caja 1: el cajero con $500 de fondo, una venta en efectivo y una con tarjeta, dos salidas iguales y una entrada.
        var turno1 = await AbrirAsync(p.Http, p.Caja.Id, p.Cajero, 50000);
        await VenderAsync(p.Http, p.Caja.Id, turno1, p.Cajero, 2500, new CobroSolicitado(FormaPago.Efectivo, 5000));
        await VenderAsync(p.Http, p.Caja.Id, turno1, p.Cajero, 1000, new CobroSolicitado(FormaPago.Tarjeta, 1000, "Aut 1"));
        await MovimientoAsync(p.Http, turno1, p.Cajero, TipoMovimientoCaja.Salida, 3000, "Garrafón");
        await MovimientoAsync(p.Http, turno1, p.Cajero, TipoMovimientoCaja.Salida, 2000, " garrafón ");
        await MovimientoAsync(p.Http, turno1, p.Cajero, TipoMovimientoCaja.Entrada, 10000, "Cambio del banco");
        // Y una de $15 que el encargado cancela: no cuenta como venta y su efectivo sale igual que entró.
        var cancelada = await VenderAsync(p.Http, p.Caja.Id, turno1, p.Cajero, 1500, new CobroSolicitado(FormaPago.Efectivo, 1500));
        await Leer<ComprobanteDevolucionDto>(await p.Http.PostAsJsonAsync($"api/ventas/{cancelada.VentaId}/cancelacion",
            new SolicitudCancelacion(p.Encargado.UsuarioId, p.Caja.Id, "Se arrepintió")));
        // Caja 2: el encargado, sin fondo, una venta en efectivo.
        var turno2 = await AbrirAsync(caja2, alta.Caja.Id, p.Encargado, 0);
        await VenderAsync(caja2, alta.Caja.Id, turno2, p.Encargado, 4000, new CobroSolicitado(FormaPago.Efectivo, 4000));

        var delCajero = await p.Http.GetAsync(Ruta(p.Cajero.UsuarioId));
        var delEncargado = await p.Http.GetAsync(Ruta(p.Encargado.UsuarioId));
        var abierto = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(Ruta(p.Dueno.UsuarioId)));
        var conTurnos = await RechazoAsync(await Cerrar());

        // Al empezar, solo el dueño ve el corte del día.
        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delEncargado.StatusCode);
        Assert.Equal((3, 7500L, 6500L, 1000L), (abierto.Tickets, abierto.VendidoCentavos, abierto.EfectivoCentavos, abierto.TarjetaCentavos));
        Assert.Equal([("Caja 1", "Cajero de prueba", 2, 3500L, 57500L), ("Caja 2", "Encargado de prueba", 1, 4000L, 4000L)],
            abierto.Turnos.Select(t => (t.Caja, t.Cajero, t.Tickets, t.VendidoCentavos, t.EsperadoCentavos!.Value)));
        Assert.All(abierto.Turnos, t => Assert.Null(t.ContadoCentavos));
        Assert.Equal([new SalidaDelDiaDto("Garrafón", 2, 5000)], abierto.Salidas);
        Assert.Equal(10000, abierto.EntradasCentavos);
        Assert.Equal((0, 1, 1500L), (abierto.Devoluciones, abierto.Cancelaciones, abierto.CancelacionesCentavos));
        Assert.Equal(["Falta cerrar el turno de Cajero de prueba (Caja 1), Encargado de prueba (Caja 2)."], abierto.PorQueNoSeCierra);
        Assert.Equal(abierto.PorQueNoSeCierra[0], conTurnos.Mensaje);

        // Los cortes: el encargado hace el de la Caja 1 (faltan $5) y el suyo en la Caja 2.
        await Leer<CorteDto>(await CortarAsync(p.Http, turno1, p.Encargado, 57000));
        await Leer<CorteDto>(await CortarAsync(caja2, turno2, p.Encargado, 4000));
        // La Caja 2 dice en su latido que todavía sube lo que hizo sin conexión; lo que diga de otra caja no cuenta.
        await caja2.GetAsync($"api/estado?caja={alta.Caja.Id}&porSubir=3&conError=0");
        await caja2.GetAsync($"api/estado?caja={p.Caja.Id}&porSubir=5&conError=0");
        var porSubir = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(Ruta(p.Dueno.UsuarioId)));
        var esperaLaSubida = await RechazoAsync(await Cerrar());
        // Ya subió; una cosa no pudo subir (queda en esa caja) y de ella no se sabe hace rato: se avisa, no detiene el cierre.
        await caja2.GetAsync($"api/estado?caja={alta.Caja.Id}&porSubir=0&conError=1");
        servicio.Fabrica.Services.GetRequiredService<PresenciaCajas>().Visto(alta.Caja.Id, DateTime.UtcNow.AddMinutes(-30));
        var listo = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(Ruta(p.Dueno.UsuarioId)));
        var cierre = await Leer<CierreDelDiaDto>(await Cerrar());
        var cerrado = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(Ruta(p.Dueno.UsuarioId)));
        var otraVez = await RechazoAsync(await Cerrar());

        Assert.Equal(["La Caja 2 todavía sube lo que hizo sin conexión (3 por subir): el cierre espera a que termine."], porSubir.PorQueNoSeCierra);
        Assert.Equal(porSubir.PorQueNoSeCierra[0], esperaLaSubida.Mensaje);
        Assert.Empty(listo.PorQueNoSeCierra);
        Assert.Equal(2, listo.Avisos.Count);
        Assert.StartsWith("La Caja 2 tiene 1 cosa de cuando estuvo sin conexión que no pudo subir", listo.Avisos[0]);
        Assert.StartsWith("No se sabe de la Caja 2 desde ", listo.Avisos[1]);
        Assert.Equal([(57000L, -500L), (4000L, 0L)], listo.Turnos.Select(t => (t.ContadoCentavos!.Value, t.DiferenciaCentavos!.Value)));
        Assert.Null(listo.Cierre);
        Assert.Equal(("Dueño de prueba", EnvioResumen.SinTelegram), (cierre.CerradoPor, cierre.Resumen));
        Assert.Equal("Dueño de prueba", cerrado.Cierre!.CerradoPor);
        Assert.StartsWith("El día ya se cerró", otraVez.Mensaje);
        await using (var db = servicio.Base())
        {
            var registro = await db.Bitacora.AsNoTracking().SingleAsync(b => b.Accion == AccionControlada.CorteDelDia);
            Assert.Equal((p.Dueno.UsuarioId, -500L, ResultadoBitacora.Hecho), (registro.UsuarioId!.Value, registro.MontoCentavos!.Value, registro.Resultado));
        }

        // Si después se abre otro turno, el día ya no está cerrado: se vuelve a cerrar al cortarlo.
        await AbrirAsync(p.Http, p.Caja.Id, p.Cajero, 50000);
        var reabierto = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(Ruta(p.Dueno.UsuarioId)));
        Assert.Null(reabierto.Cierre);
        Assert.Equal(3, reabierto.Turnos.Count);
        Assert.Single(reabierto.PorQueNoSeCierra);
    }

    /// <summary>
    /// Los días se cuentan por lo que pasó en ellos: un turno que viene de ayer cuenta hoy solo lo de hoy (lo esperado es de
    /// todo el turno), y si se corta hoy, su diferencia es de hoy. Lo que una caja dijo que tenía por subir y dejó de decir
    /// solo se avisa. Un turno abierto que llega después del cierre (de una caja que estaba sin conexión) vuelve a abrir el
    /// día.
    /// </summary>
    [Fact]
    public async Task Un_turno_que_viene_de_ayer_cuenta_hoy_lo_de_hoy_y_su_corte_es_de_hoy()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var p = await PuestoAsync(servicio);
        string Ruta(Guid usuario) => $"api/cajas/{p.Caja.Id}/corte-del-dia?usuarioId={usuario}";
        Task<HttpResponseMessage> Cerrar() => p.Http.PostAsJsonAsync($"api/cajas/{p.Caja.Id}/corte-del-dia/cierre", new SolicitudCierreDia(p.Dueno.UsuarioId));
        var presencia = servicio.Fabrica.Services.GetRequiredService<PresenciaCajas>();

        // El cajero abrió ayer con $500, vendió $25 en efectivo y sacó $30; el turno se quedó abierto.
        var turno = await AbrirAsync(p.Http, p.Caja.Id, p.Cajero, 50000);
        var deAyer = await VenderAsync(p.Http, p.Caja.Id, turno, p.Cajero, 2500, new CobroSolicitado(FormaPago.Efectivo, 2500));
        await MovimientoAsync(p.Http, turno, p.Cajero, TipoMovimientoCaja.Salida, 3000, "Garrafón");
        await using (var db = servicio.Base())
        {
            var abiertoUtc = db.Entry((await db.Turnos.FindAsync(turno.Id))!).Property(t => t.AbiertoUtc);
            abiertoUtc.CurrentValue = abiertoUtc.CurrentValue.AddDays(-1);
            var pagada = db.Entry((await db.Ventas.FindAsync(deAyer.VentaId))!).Property(v => v.PagadaUtc);
            pagada.CurrentValue = pagada.CurrentValue!.Value.AddDays(-1);
            var salida = await db.MovimientosCaja.SingleAsync(m => m.TurnoId == turno.Id);
            salida.FechaUtc = salida.FechaUtc.AddDays(-1);
            await db.SaveChangesAsync();
        }
        // Hoy: $10 con tarjeta y una salida de $20.
        await VenderAsync(p.Http, p.Caja.Id, turno, p.Cajero, 1000, new CobroSolicitado(FormaPago.Tarjeta, 1000, "Aut 1"));
        await MovimientoAsync(p.Http, turno, p.Cajero, TipoMovimientoCaja.Salida, 2000, "Papel de baño");
        // La Caja 1 dijo hace 5 minutos que tenía 3 por subir y no ha vuelto a decir nada.
        presencia.Bandeja(p.Caja.Id, 3, 0, DateTime.UtcNow.AddMinutes(-5));

        var abierto = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(Ruta(p.Dueno.UsuarioId)));
        await Leer<CorteDto>(await CortarAsync(p.Http, turno, p.Encargado, 44000));
        var cortado = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(Ruta(p.Dueno.UsuarioId)));
        CorteDelDiaDto deAyerDia;
        await using (var db = servicio.Base())
        {
            var registro = new RegistroCorteDelDia(db);
            deAyerDia = await registro.ArmarAsync(p.Caja.SucursalId, registro.Hoy.AddDays(-1), presencia.Foto());
        }

        // Hoy: solo el ticket de hoy y la salida de hoy; lo esperado es de todo el turno (500 + 25 − 30 − 20).
        var fila = Assert.Single(abierto.Turnos);
        Assert.Equal((1, 1000L, 1000L, 47500L), (fila.Tickets, fila.VendidoCentavos, fila.TarjetaCentavos, fila.EsperadoCentavos!.Value));
        Assert.Equal((1, 1000L, 0L, 1000L), (abierto.Tickets, abierto.VendidoCentavos, abierto.EfectivoCentavos, abierto.TarjetaCentavos));
        Assert.Equal([new SalidaDelDiaDto("Papel de baño", 1, 2000)], abierto.Salidas);
        // Lo que la caja dejó de decir no detiene el cierre: se avisa.
        Assert.Equal(["Falta cerrar el turno de Cajero de prueba (Caja 1)."], abierto.PorQueNoSeCierra);
        Assert.Contains(abierto.Avisos, a => a.StartsWith("La Caja 1 tenía 3 por subir de cuando estuvo sin conexión y no avisa desde "));
        // Cortado hoy, su diferencia es de hoy (44000 − 47500), aunque se abrió ayer.
        var cortada = Assert.Single(cortado.Turnos);
        Assert.Equal((44000L, -3500L), (cortada.ContadoCentavos!.Value, cortada.DiferenciaCentavos!.Value));
        Assert.Empty(cortado.PorQueNoSeCierra);
        // Ayer: el mismo turno, con lo de ayer.
        var deAyerFila = deAyerDia.Turnos.Single(t => t.Id == turno.Id);
        Assert.Equal((1, 2500L), (deAyerFila.Tickets, deAyerFila.VendidoCentavos));
        Assert.Contains(deAyerDia.Salidas, s => s.Motivo == "Garrafón" && s.MontoCentavos == 3000);
        Assert.DoesNotContain(deAyerDia.Salidas, s => s.Motivo == "Papel de baño");

        // Se cierra el día. Después sube el turno de una caja que estaba sin conexión, abierto antes del cierre: el día ya
        // no está cerrado (antes seguía diciendo «Día cerrado» con el turno abierto en la lista).
        await Leer<CierreDelDiaDto>(await Cerrar());
        var otro = await AbrirAsync(p.Http, p.Caja.Id, p.Cajero, 10000);
        await using (var db = servicio.Base())
        {
            var abiertoUtc = db.Entry((await db.Turnos.FindAsync(otro.Id))!).Property(t => t.AbiertoUtc);
            abiertoUtc.CurrentValue = abiertoUtc.CurrentValue.AddMinutes(-30);
            await db.SaveChangesAsync();
        }
        var conElQueSubio = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(Ruta(p.Dueno.UsuarioId)));
        Assert.Null(conElQueSubio.Cierre);
        Assert.Equal(["Falta cerrar el turno de Cajero de prueba (Caja 1)."], conElQueSubio.PorQueNoSeCierra);
    }
}

/// <summary>Anota la ruta de cada llamada de la caja al servicio (para ver lo que dice en su latido).</summary>
internal sealed class Anotador(HttpMessageHandler interno) : DelegatingHandler(interno)
{
    public List<string> Rutas { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Rutas)
            Rutas.Add(request.RequestUri!.PathAndQuery);
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>El resumen del día sale con el último corte, como siempre; cerrar el día no lo manda otra vez.</summary>
public sealed class CorteDelDiaTelegramTests(ServicioDePrueba servicio) : PruebaTelegram(servicio)
{
    [Fact]
    public async Task El_resumen_del_dia_sale_una_vez_aunque_tambien_se_cierre_el_dia()
    {
        await VincularAsync();
        var caja = await CajaAsync();
        var http = Http();
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        Task<HttpResponseMessage> Cerrar() => http.PostAsJsonAsync($"api/cajas/{caja.Id}/corte-del-dia/cierre", new SolicitudCierreDia(dueno.UsuarioId));

        // Un día sin turnos: el resumen sale al cerrarlo.
        var sinTurnos = await Leer<CierreDelDiaDto>(await Cerrar());
        // Después se abre un turno; su corte, el último del día, manda el resumen como siempre (y la diferencia pasa del
        // umbral de $50). Cerrar el día otra vez no lo repite.
        var (_, _, turno) = await TurnoAsync();
        var corte = await Leer<CorteDto>(await http.PostAsJsonAsync($"api/turnos/{turno.Id}/corte-z", new SolicitudCorteZ(encargado.UsuarioId, 40000)));
        var conCorte = await Leer<CierreDelDiaDto>(await Cerrar());
        await Motor.EnviarAsync();

        Assert.Equal(EnvioResumen.Enviado, sinTurnos.Resumen);
        Assert.Equal(EnvioResumen.YaHabiaSalido, conCorte.Resumen);
        Assert.Equal(5000, corte.UmbralAvisoCentavos);
        Assert.Equal(2, MensajesA(ChatDueno).Count(t => t.StartsWith("Resumen del día")));
        Assert.Single(MensajesA(ChatDueno), t => t.StartsWith("Diferencia en el corte · Caja 1") && t.Contains("Corte del turno de Cajero de prueba"));
    }
}

/// <summary>
/// Tampoco sale dos veces cuando el último corte en llegar no es el último por la hora: el de una caja que cortó sin
/// conexión sube después, con la hora de entonces, y el resumen sale con él al subir.
/// </summary>
public sealed class CorteDelDiaTelegramSinConexionTests(ServicioDePrueba servicio) : PruebaTelegram(servicio)
{
    [Fact]
    public async Task El_resumen_no_se_repite_si_el_ultimo_corte_en_llegar_se_hizo_antes_sin_conexion()
    {
        await VincularAsync();
        var caja = await CajaAsync();
        var http = Http();
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        var alta = await Leer<AltaCajaDto>(await http.PostAsJsonAsync("api/alta",
            new SolicitudAltaCaja("dueno", "3333", caja.SucursalId, null, "Caja 2", "B", "PC-2")));
        var caja2 = Fabrica.CreateClient();
        caja2.DefaultRequestHeaders.Add(ClienteCaja.EncabezadoLlave, alta.Llave);
        var (_, _, turno1) = await TurnoAsync();
        var turno2 = await Leer<TurnoDto>(await caja2.PostAsJsonAsync("api/turnos", new SolicitudTurno(alta.Caja.Id, encargado.UsuarioId, 0)));

        // La Caja 2 corta sin conexión; un cuarto de hora después la Caja 1 corta en línea (con la Caja 2 abierta en el
        // servicio, no sale el resumen). Luego sube el corte de la Caja 2, con su hora de antes, y el resumen sale con él.
        await Leer<CorteDto>(await http.PostAsJsonAsync($"api/turnos/{turno1.Id}/corte-z", new SolicitudCorteZ(encargado.UsuarioId, 50000)));
        await Leer<CorteDto>(await caja2.PostAsJsonAsync($"api/bandeja/turnos/{turno2.Id}/corte-z",
            new CorteContingenciaDto(Ids.Nuevo(), encargado.UsuarioId, null, DateTime.UtcNow.AddMinutes(-15), 0)));
        var cierre = await Leer<CierreDelDiaDto>(await http.PostAsJsonAsync($"api/cajas/{caja.Id}/corte-del-dia/cierre", new SolicitudCierreDia(dueno.UsuarioId)));
        await Motor.EnviarAsync();

        Assert.Equal(EnvioResumen.YaHabiaSalido, cierre.Resumen);
        Assert.Single(MensajesA(ChatDueno), t => t.StartsWith("Resumen del día"));
    }
}
