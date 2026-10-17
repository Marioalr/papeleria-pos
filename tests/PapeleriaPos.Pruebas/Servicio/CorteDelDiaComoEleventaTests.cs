using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PapeleriaPos.Datos;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El corte del día como el de eleventa (2026-10-17, docs/paridad-eleventa.md): el de otro día solo se ve (y el de hoy sigue
/// con sus reglas para cerrarse), las ventas por departamento para todos los que ven el corte y la utilidad solo para quien
/// tiene «Ver costos, utilidad y margen», los turnos del piloto que cerró el corte de eleventa siguen diciéndolo en el día
/// que les toca, y el recordatorio de cerrar el día sale desde la hora de cierre de la tienda, a quien lo cierra, hasta que
/// se cierra. Cada prueba con su base.
/// </summary>
public sealed class CorteDelDiaComoEleventaTests
{
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

    private sealed record Puesto(HttpClient Http, CajaDto Caja, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno)
    {
        public string Ruta(SesionDto quien, DateOnly? fecha = null) =>
            $"api/cajas/{Caja.Id}/corte-del-dia?usuarioId={quien.UsuarioId}"
            + (fecha is { } f ? $"&fecha={f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" : "");

        public Task<HttpResponseMessage> CerrarAsync() =>
            Http.PostAsJsonAsync($"api/cajas/{Caja.Id}/corte-del-dia/cierre", new SolicitudCierreDia(Dueno.UsuarioId));

        public async Task CambiarAsync(SesionDto de, AccionControlada accion, NivelPermiso nivel) =>
            await Leer<UsuariosYPermisosDto>(await Http.PutAsJsonAsync($"api/cajas/{Caja.Id}/usuarios-y-permisos/{de.UsuarioId}",
                new SolicitudPermisoUsuario(Dueno.UsuarioId, accion, nivel)));
    }

    private static async Task<Puesto> PuestoAsync(ServicioDePrueba servicio)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        async Task<SesionDto> Entrar(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        return new Puesto(http, caja, await Entrar("cajero", "1111"), await Entrar("encargado", "2222"), await Entrar("dueno", "3333"));
    }

    private static async Task<TurnoDto> AbrirAsync(Puesto p, SesionDto cajero, long fondo = 50000) =>
        await Leer<TurnoDto>(await p.Http.PostAsJsonAsync("api/turnos", new SolicitudTurno(p.Caja.Id, cajero.UsuarioId, fondo)));

    private static async Task<TicketDto> VenderAsync(Puesto p, TurnoDto turno, IReadOnlyList<LineaSolicitada> lineas, CobroSolicitado cobro) =>
        await Leer<TicketDto>(await p.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(p.Caja.Id, turno.Id, turno.CajeroId, lineas, [cobro])));

    /// <summary>Un artículo común por <paramref name="precio"/>.</summary>
    private static Task<TicketDto> VenderComunAsync(Puesto p, TurnoDto turno, long precio, CobroSolicitado cobro) =>
        VenderAsync(p, turno, [new LineaSolicitada(null, null, 1000, "Recuerdo", precio)], cobro);

    private static Task<HttpResponseMessage> CortarAsync(Puesto p, TurnoDto turno, SesionDto quien, long contado) =>
        p.Http.PostAsJsonAsync($"api/turnos/{turno.Id}/corte-z", new SolicitudCorteZ(quien.UsuarioId, contado));

    /// <summary>El primer producto que encuentra la búsqueda.</summary>
    private static async Task<Guid> VarianteAsync(Puesto p, string texto) =>
        (await p.Http.GetFromJsonAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q={Uri.EscapeDataString(texto)}&sucursalId={p.Caja.SucursalId}"))!
            .First().VarianteId;

    private static DateOnly Hoy => DateOnly.FromDateTime(HoraMexico.ALocal(DateTime.UtcNow));

    /// <summary>La hora de México de ese día, en UTC.</summary>
    private static DateTime A(DateOnly dia, int hora, int minuto = 0) => HoraMexico.AUtc(dia.ToDateTime(new TimeOnly(hora, minuto)));

    /// <summary>
    /// Pasa a <paramref name="dia"/> el turno: se abrió a las 10:00, cobró a las 12:00, lo demás fue a las 13:00 y, si ya se
    /// cortó, se cortó a las 18:00 (a horas fijas: la prueba no depende de la hora en que corre). Con <paramref name="cerro"/>, a
    /// las 18:05 se cerró el día (la bitácora solo se agrega: el cierre se anota ya con su hora).
    /// </summary>
    private static async Task PasarAAsync(ServicioDePrueba servicio, Guid turnoId, DateOnly dia, SesionDto? cerro = null)
    {
        await using var db = servicio.Base();
        var turno = await db.Turnos.SingleAsync(t => t.Id == turnoId);
        turno.AbiertoUtc = A(dia, 10);
        if (turno.CerradoUtc is not null)
            turno.CerradoUtc = A(dia, 18);
        foreach (var venta in await db.Ventas.Where(v => v.TurnoId == turnoId).ToListAsync())
        {
            venta.CreadaUtc = A(dia, 12);
            db.Entry(venta).Property(v => v.PagadaUtc).CurrentValue = A(dia, 12);
        }
        foreach (var movimiento in await db.MovimientosCaja.Where(m => m.TurnoId == turnoId).ToListAsync())
            movimiento.FechaUtc = A(dia, 13);
        foreach (var corte in await db.Cortes.Where(c => c.TurnoId == turnoId).ToListAsync())
            corte.FechaUtc = A(dia, 18);
        if (cerro is not null)
            db.Bitacora.Add(new Bitacora
            {
                SucursalId = turno.SucursalId, CajaId = turno.CajaId, UsuarioId = cerro.UsuarioId, Accion = AccionControlada.CorteDelDia,
                Detalle = "Cerró el día", Resultado = ResultadoBitacora.Hecho, FechaUtc = A(dia, 18, 5),
            });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// El de otro día se ve con lo que pasó ese día y si se cerró, sin lo de hoy (ni lo que falta para cerrar hoy); el de hoy
    /// sigue con sus reglas. Uno que no ha llegado no se pide, y solo se cierra el de hoy.
    /// </summary>
    [Fact]
    public async Task El_de_otro_dia_solo_se_ve_y_el_de_hoy_sigue_con_sus_reglas()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var p = await PuestoAsync(servicio);
        // Ayer: el cajero abrió con $500, vendió $25 en efectivo y sacó $30 para el garrafón; el encargado hizo su corte (faltaron
        // $5) y el dueño cerró el día.
        var deAyer = await AbrirAsync(p, p.Cajero);
        await VenderComunAsync(p, deAyer, 2500, new CobroSolicitado(FormaPago.Efectivo, 5000));
        await Leer<ComprobanteMovimientoDto>(await p.Http.PostAsJsonAsync($"api/turnos/{deAyer.Id}/movimientos",
            new SolicitudMovimiento(TipoMovimientoCaja.Salida, 3000, "Garrafón", p.Cajero.UsuarioId)));
        await Leer<CorteDto>(await CortarAsync(p, deAyer, p.Encargado, 49000));
        var (hoy, ayer) = (Hoy, Hoy.AddDays(-1));
        await PasarAAsync(servicio, deAyer.Id, ayer, cerro: p.Dueno);
        // Hoy: otro turno del cajero, abierto, con $10 con tarjeta.
        var deHoy = await AbrirAsync(p, p.Cajero);
        await VenderComunAsync(p, deHoy, 1000, new CobroSolicitado(FormaPago.Tarjeta, 1000, "Aut 1"));

        var corteDeHoy = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(p.Ruta(p.Dueno)));
        var corteDeAyer = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(p.Ruta(p.Dueno, ayer)));
        var corteDeHoyConFecha = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(p.Ruta(p.Dueno, hoy)));
        var deManana = await RechazoAsync(await p.Http.GetAsync(p.Ruta(p.Dueno, hoy.AddDays(1))));
        var cerrarHoy = await RechazoAsync(await p.CerrarAsync());
        var delCajero = await p.Http.GetAsync(p.Ruta(p.Cajero, ayer));

        // Hoy: solo lo de hoy, con lo que falta para cerrarlo.
        Assert.Equal((hoy, false), (corteDeHoy.Fecha, corteDeHoy.DeOtroDia));
        var turnoDeHoy = Assert.Single(corteDeHoy.Turnos);
        Assert.Equal((deHoy.Id, 1, 1000L), (turnoDeHoy.Id, turnoDeHoy.Tickets, turnoDeHoy.TarjetaCentavos));
        Assert.Equal(["Falta cerrar el turno de Cajero de prueba (Caja 1)."], corteDeHoy.PorQueNoSeCierra);
        Assert.Null(corteDeHoy.Cierre);
        Assert.Equal((corteDeHoy.Fecha, corteDeHoy.DeOtroDia, corteDeHoy.Tickets), (corteDeHoyConFecha.Fecha, corteDeHoyConFecha.DeOtroDia, corteDeHoyConFecha.Tickets));
        // Ayer: lo de ayer, su corte con su diferencia y que se cerró, aunque hoy haya un turno abierto; nada de lo de hoy.
        Assert.Equal((ayer, true), (corteDeAyer.Fecha, corteDeAyer.DeOtroDia));
        var turnoDeAyer = Assert.Single(corteDeAyer.Turnos);
        Assert.Equal((deAyer.Id, 1, 2500L, 49000L, -500L),
            (turnoDeAyer.Id, turnoDeAyer.Tickets, turnoDeAyer.VendidoCentavos, turnoDeAyer.ContadoCentavos!.Value, turnoDeAyer.DiferenciaCentavos!.Value));
        Assert.Equal((1, 2500L, 2500L, 0L), (corteDeAyer.Tickets, corteDeAyer.VendidoCentavos, corteDeAyer.EfectivoCentavos, corteDeAyer.TarjetaCentavos));
        Assert.Equal([new SalidaDelDiaDto("Garrafón", 1, 3000)], corteDeAyer.Salidas);
        Assert.Empty(corteDeAyer.PorQueNoSeCierra);
        Assert.Empty(corteDeAyer.Avisos);
        Assert.Equal("Dueño de prueba", corteDeAyer.Cierre?.CerradoPor);
        // Un día que no ha llegado no se pide; y cerrar sigue siendo el de hoy, con sus reglas.
        Assert.Equal(RegistroCorteDelDia.TodaviaNoLlega, deManana.Mensaje);
        Assert.Equal("Falta cerrar el turno de Cajero de prueba (Caja 1).", cerrarHoy.Mensaje);
        // El de otro día, también solo para quien tiene el permiso.
        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        await using (var db = servicio.Base())
        {
            var registro = new RegistroCorteDelDia(db);
            Assert.Equal(RegistroCorteDelDia.SoloHoySeCierra, (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                registro.CerrarAsync(p.Caja.SucursalId, p.Caja.Id, p.Dueno.UsuarioId, ayer, new Dictionary<Guid, PresenciaDeCaja>()))).Message);
        }

        // Un día que ya pasó se ve según cómo fue: ayer no fue del piloto, así que en el piloto de hoy sigue diciendo que se cerró.
        await using (var conPiloto = servicio.Fabrica.WithWebHostBuilder(b =>
        {
            foreach (var (clave, valor) in Piloto())
                b.UseSetting(clave, valor);
        }))
        {
            var http = conPiloto.CreateClient();
            var ayerEnElPiloto = await Leer<CorteDelDiaDto>(await http.GetAsync(p.Ruta(p.Dueno, ayer)));
            var hoyEnElPiloto = await Leer<CorteDelDiaDto>(await http.GetAsync(p.Ruta(p.Dueno)));
            Assert.Equal((false, "Dueño de prueba"), (ayerEnElPiloto.Piloto, ayerEnElPiloto.Cierre?.CerradoPor));
            Assert.True(hoyEnElPiloto.Piloto);
        }

        // Si después del cierre de ayer se abrió otro turno ese mismo día (el de hoy viene de anoche), ayer ya no está cerrado.
        await using (var db = servicio.Base())
        {
            var turno = await db.Turnos.SingleAsync(t => t.Id == deHoy.Id);
            turno.AbiertoUtc = A(ayer, 23, 30);
            await db.SaveChangesAsync();
        }
        var reabierto = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(p.Ruta(p.Dueno, ayer)));
        Assert.Null(reabierto.Cierre);
        Assert.Equal(2, reabierto.Turnos.Count);
        Assert.Empty(reabierto.PorQueNoSeCierra);
    }

    /// <summary>
    /// «Cerrar el día» cierra el día que se ve en la caja, y solo si es hoy: si pasó la medianoche con el corte abierto, la
    /// caja manda el de ayer y el servicio no cierra nada (ni ese ni el nuevo, que nadie ha visto). Antes el servicio cerraba
    /// siempre el de hoy. Una caja de antes no manda el día: es hoy.
    /// </summary>
    [Fact]
    public async Task Cerrar_el_dia_es_solo_el_de_hoy_aunque_la_caja_mande_otro()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var p = await PuestoAsync(servicio);
        var (hoy, ayer) = (Hoy, Hoy.AddDays(-1));
        var turno = await AbrirAsync(p, p.Cajero);
        await Leer<CorteDto>(await CortarAsync(p, turno, p.Encargado, 50000));
        Task<HttpResponseMessage> CerrarAsync(DateOnly? fecha) =>
            p.Http.PostAsJsonAsync($"api/cajas/{p.Caja.Id}/corte-del-dia/cierre", new SolicitudCierreDia(p.Dueno.UsuarioId, fecha));
        async Task<int> CierresAsync()
        {
            await using var db = servicio.Base();
            return await db.Bitacora.CountAsync(b => b.Accion == AccionControlada.CorteDelDia && b.Resultado == ResultadoBitacora.Hecho);
        }

        var deAyer = await RechazoAsync(await CerrarAsync(ayer));
        var deManana = await RechazoAsync(await CerrarAsync(hoy.AddDays(1)));
        var antes = await CierresAsync();
        var deHoy = await Leer<CierreDelDiaDto>(await CerrarAsync(hoy));

        Assert.Equal(RegistroCorteDelDia.SoloHoySeCierra, deAyer.Mensaje);
        Assert.Equal(RegistroCorteDelDia.SoloHoySeCierra, deManana.Mensaje);
        Assert.Equal(0, antes);
        Assert.Equal("Dueño de prueba", deHoy.CerradoPor);
        Assert.Equal(1, await CierresAsync());
        Assert.Equal("Dueño de prueba", (await Leer<CorteDelDiaDto>(await p.Http.GetAsync(p.Ruta(p.Dueno)))).Cierre?.CerradoPor);
    }

    /// <summary>
    /// En el piloto, lo que se devolvió en eleventa de un ticket de la caja 2 entra en la venta de aquí sin efectivo y en el
    /// turno de esa venta, que pudo cortarse antes. Se resta de lo vendido del día en que se devolvió (antes no se restaba de
    /// ningún día: el corte buscaba las devoluciones por sus turnos), y no toca el efectivo de ningún turno.
    /// </summary>
    [Fact]
    public async Task Lo_devuelto_en_eleventa_de_un_turno_ya_cortado_se_resta_del_dia_en_que_se_devolvio()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var p = await PuestoAsync(servicio);
        var (hoy, ayer) = (Hoy, Hoy.AddDays(-1));
        // Ayer: dos plumas en un turno que ya se cortó; la venta llegó a eleventa (su ticket 500).
        var turno = await AbrirAsync(p, p.Cajero);
        var pluma = await VarianteAsync(p, "pluma punto fino");
        var ticket = await VenderAsync(p, turno, [new(pluma, null, 2000)], new CobroSolicitado(FormaPago.Efectivo, 100000));
        await Leer<CorteDto>(await CortarAsync(p, turno, p.Encargado, 50000 + ticket.TotalCentavos));
        await PasarAAsync(servicio, turno.Id, ayer);
        VentaLinea linea;
        await using (var db = servicio.Base())
        {
            linea = await db.VentasLineas.AsNoTracking().SingleAsync(l => l.VentaId == ticket.VentaId);
            db.EnviosAEleventa.Add(new EnvioAEleventa
            {
                Tipo = TipoEnvioEleventa.Venta, VentaId = ticket.VentaId, Estado = EstadoEnvioEleventa.Enviada, CreadoUtc = A(ayer, 12), EnviadaUtc = A(ayer, 12),
                TicketEleventa = 500, FolioEleventa = 42,
            });
            await db.SaveChangesAsync();
        }
        // Hoy, con otro turno abierto con $500: en eleventa devolvieron una pluma de ese ticket, y la vuelta la pasa aquí.
        await AbrirAsync(p, p.Cajero);
        var precio = linea.PrecioCentavos / 100m;
        await using (var db = servicio.Base())
            Assert.Equal(1, (await DevolucionesConEleventa.PasarVueltaAsync(db,
                [new DevolucionLeida(7, 500, false, hoy.ToDateTime(new TimeOnly(10, 0)), precio, "Ana", "Principal", 31,
                    [new ArticuloDevueltoLeido("PLUMA", linea.Descripcion, 1m, precio, null)])], DateTime.UtcNow)).Pasadas);
        Devolucion devolucion;
        await using (var db = servicio.Base())
            devolucion = await db.Devoluciones.AsNoTracking().Include(d => d.Lineas).SingleAsync();

        var corteDeHoy = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(p.Ruta(p.Dueno)));
        var corteDeAyer = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(p.Ruta(p.Dueno, ayer)));

        // Quedó en el turno de la venta, de ayer y ya cortado, sin efectivo de aquí.
        Assert.Equal((turno.Id, 0L), (devolucion.TurnoId, devolucion.ReembolsoEfectivoCentavos));
        var devuelta = Assert.Single(devolucion.Lineas);
        // Hoy se resta de lo vendido de su departamento y de la utilidad, y sale en las devoluciones del día.
        Assert.Equal((1, devolucion.TotalCentavos), (corteDeHoy.Devoluciones, corteDeHoy.DevolucionesCentavos));
        var departamento = Assert.Single(corteDeHoy.PorDepartamento!.Departamentos);
        Assert.Equal((-devuelta.ImporteCentavos, (long?)-devuelta.CostoCentavos), (departamento.VentaCentavos, departamento.CostoCentavos));
        Assert.Equal(-(devuelta.ImporteCentavos - devuelta.CostoCentavos), corteDeHoy.Utilidad!.UtilidadCentavos);
        // El efectivo del turno de hoy no cambia: esa devolución no salió de esta caja.
        Assert.Equal(50000, Assert.Single(corteDeHoy.Turnos).EsperadoCentavos);
        // Ayer sigue con sus dos plumas.
        Assert.Equal(0, corteDeAyer.Devoluciones);
        Assert.Equal(linea.ImporteCentavos, Assert.Single(corteDeAyer.PorDepartamento!.Departamentos).VentaCentavos);
    }

    /// <summary>
    /// Un turno que se abrió ayer y se corta hoy: su corte (lo contado y la diferencia) va en el día en que se hizo. Ayer cuenta
    /// lo que vendió ayer y dice que se cerró hoy, sin la diferencia, que así no se cuenta dos veces.
    /// </summary>
    [Fact]
    public async Task Un_turno_que_se_corta_otro_dia_lleva_su_corte_en_ese_dia()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var p = await PuestoAsync(servicio);
        var (hoy, ayer) = (Hoy, Hoy.AddDays(-1));
        // Ayer a las 20:00 abrió con $500 y vendió $25 en efectivo a las 20:30; el turno se quedó abierto.
        var turno = await AbrirAsync(p, p.Cajero);
        var ticket = await VenderComunAsync(p, turno, 2500, new CobroSolicitado(FormaPago.Efectivo, 2500));
        await using (var db = servicio.Base())
        {
            (await db.Turnos.SingleAsync(t => t.Id == turno.Id)).AbiertoUtc = A(ayer, 20);
            var venta = await db.Ventas.SingleAsync(v => v.Id == ticket.VentaId);
            venta.CreadaUtc = A(ayer, 20, 30);
            db.Entry(venta).Property(v => v.PagadaUtc).CurrentValue = A(ayer, 20, 30);
            await db.SaveChangesAsync();
        }
        // Hoy se corta: faltaron $5.
        await Leer<CorteDto>(await CortarAsync(p, turno, p.Encargado, 52000));

        var deAyer = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(p.Ruta(p.Dueno, ayer)));
        var deHoy = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(p.Ruta(p.Dueno)));

        var ayerTurno = Assert.Single(deAyer.Turnos);
        Assert.Equal((1, 2500L), (ayerTurno.Tickets, ayerTurno.VendidoCentavos));
        Assert.Equal(((long?)null, (long?)null, (long?)null), (ayerTurno.EsperadoCentavos, ayerTurno.ContadoCentavos, ayerTurno.DiferenciaCentavos));
        Assert.Equal(hoy, DateOnly.FromDateTime(ayerTurno.CerradoLocal!.Value));
        var hoyTurno = Assert.Single(deHoy.Turnos);
        Assert.Equal((0, 52500L, 52000L, -500L), (hoyTurno.Tickets, hoyTurno.EsperadoCentavos!.Value, hoyTurno.ContadoCentavos!.Value, hoyTurno.DiferenciaCentavos!.Value));
    }

    /// <summary>
    /// Lo vendido por departamento, como en el corte de eleventa, para todos los que ven el corte del día; la utilidad (la del
    /// día y la de cada departamento), como la ganancia de eleventa, solo para quien tiene «Ver costos, utilidad y margen» (al
    /// momento de dárselo): a los demás no les llega ningún costo. Lo vendido menos su costo de cuando se cobró; lo devuelto
    /// ese día se resta de su departamento; el artículo común no entra y los servicios cuentan sin costo, como hasta hoy.
    /// </summary>
    [Fact]
    public async Task Los_departamentos_los_ve_quien_ve_el_corte_y_la_utilidad_solo_quien_la_ve()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var p = await PuestoAsync(servicio);
        // El encargado ve el corte del día, pero no la utilidad.
        await p.CambiarAsync(p.Encargado, AccionControlada.CorteDelDia, NivelPermiso.Permitido);
        var turno = await AbrirAsync(p, p.Cajero);
        var pluma = await VarianteAsync(p, "pluma punto fino");
        var cuaderno = await VarianteAsync(p, "cuaderno profesional raya");
        var copia = await VarianteAsync(p, "copia blanco y negro");
        var ticket = await VenderAsync(p, turno,
            [new(pluma, null, 2000), new(cuaderno, null, 1000), new(copia, null, 3000), new(null, null, 1000, "Recuerdo", 1500)],
            new CobroSolicitado(FormaPago.Efectivo, 200000));
        // Devuelven una pluma: sale de lo vendido de su departamento y de su costo.
        List<VentaLinea> lineas;
        await using (var db = servicio.Base())
            lineas = await db.VentasLineas.AsNoTracking().Where(l => l.VentaId == ticket.VentaId).ToListAsync();
        var lineaPluma = lineas.Single(l => l.VarianteId == pluma);
        await Leer<ComprobanteDevolucionDto>(await p.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
            new SolicitudDevolucion(p.Dueno.UsuarioId, p.Caja.Id, "No le gustó", [new LineaDevolucionSolicitada(lineaPluma.Id, 1000)])));

        var crudoDelEncargado = await p.Http.GetStringAsync(p.Ruta(p.Encargado));
        var delEncargado = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(p.Ruta(p.Encargado)));
        var delDueno = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(p.Ruta(p.Dueno)));
        var delCajero = await p.Http.GetAsync(p.Ruta(p.Cajero));

        // Lo que se espera, con los costos que guardó la venta al cobrar y el de la pluma devuelta.
        DevolucionLinea devuelta;
        Dictionary<Guid, string> departamentoDe;
        await using (var db = servicio.Base())
        {
            devuelta = await db.DevolucionesLineas.AsNoTracking().SingleAsync();
            var variantes = lineas.Select(l => l.VarianteId).OfType<Guid>().ToList();
            departamentoDe = await db.Variantes.AsNoTracking().Where(v => variantes.Contains(v.Id))
                .Select(v => new { v.Id, v.Producto!.Departamento!.Nombre }).ToDictionaryAsync(v => v.Id, v => v.Nombre);
        }
        var lineaCuaderno = lineas.Single(l => l.VarianteId == cuaderno);
        var lineaCopia = lineas.Single(l => l.VarianteId == copia);
        var lineaComun = lineas.Single(l => l.VarianteId is null);
        Assert.True(lineaPluma.CostoCentavos > 0 && lineaCuaderno.CostoCentavos > 0, "La mercancía se cobra con su costo del kardex.");
        Assert.Equal((0L, false), (lineaCopia.CostoCentavos, lineaCopia.LlevaInventario));
        Assert.Equal((lineaPluma.ImporteCentavos / 2, lineaPluma.CostoCentavos / 2), (devuelta.ImporteCentavos, devuelta.CostoCentavos));
        var esperados = new[]
            {
                (departamentoDe[pluma], lineaPluma.ImporteCentavos - devuelta.ImporteCentavos, lineaPluma.CostoCentavos - devuelta.CostoCentavos),
                (departamentoDe[cuaderno], lineaCuaderno.ImporteCentavos, lineaCuaderno.CostoCentavos),
                (departamentoDe[copia], lineaCopia.ImporteCentavos, 0L),
            }
            .GroupBy(x => x.Item1).Select(g => new DepartamentoDelDiaDto(g.Key, g.Sum(x => x.Item2), g.Sum(x => x.Item3)))
            .OrderByDescending(d => d.VentaCentavos).ThenBy(d => d.Departamento, StringComparer.CurrentCultureIgnoreCase).ToList();

        // Quien ve el corte pero no la utilidad: lo vendido por departamento, igual que el dueño, sin ningún costo (ni vacío
        // en lo que llega) y sin la utilidad.
        Assert.Null(delEncargado.Utilidad);
        Assert.Equal(esperados.Select(d => d with { CostoCentavos = null }), delEncargado.PorDepartamento!.Departamentos);
        Assert.Equal((0L, (long?)null, 1500L), (delEncargado.PorDepartamento.ImpresionesCentavos, delEncargado.PorDepartamento.ImpresionesCostoCentavos,
            delEncargado.PorDepartamento.ArticuloComunCentavos));
        Assert.Contains("\"departamentos\"", crudoDelEncargado, StringComparison.OrdinalIgnoreCase);
        foreach (var dato in new[] { "costo", "utilidadCentavos", "margen" })
            Assert.DoesNotContain(dato, crudoDelEncargado, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((1, 1), (delEncargado.Tickets, delEncargado.Devoluciones));
        Assert.Equal(HttpStatusCode.Forbidden, delCajero.StatusCode);
        // El dueño: por departamento, de lo que más se vendió a lo que menos, con lo devuelto ya restado, y con su costo.
        var porDepartamento = delDueno.PorDepartamento!;
        Assert.Equal(esperados, porDepartamento.Departamentos);
        Assert.Equal((0L, (long?)0L), (porDepartamento.ImpresionesCentavos, porDepartamento.ImpresionesCostoCentavos));
        // El artículo común va aparte y no entra en la utilidad; los servicios, sin costo.
        var utilidad = delDueno.Utilidad!;
        Assert.Equal((lineaComun.ImporteCentavos, lineaCopia.ImporteCentavos), (porDepartamento.ArticuloComunCentavos, utilidad.ServiciosSinCostoCentavos));
        var venta = esperados.Sum(d => d.VentaCentavos);
        var costo = esperados.Sum(d => d.CostoCentavos!.Value);
        Assert.Equal((venta, costo, venta - costo), (utilidad.VentaCentavos, utilidad.CostoCentavos, utilidad.UtilidadCentavos));
        Assert.Equal(Margen.Calcular(venta, costo), utilidad.MargenCentesimas);
        Assert.Equal(esperados.Select(d => d.VentaCentavos - d.CostoCentavos), porDepartamento.Departamentos.Select(d => d.UtilidadCentavos));
        // Todo lo vendido, menos lo devuelto: lo del corte.
        Assert.Equal(delDueno.VendidoCentavos - delDueno.DevolucionesCentavos, porDepartamento.TotalCentavos);

        // En cuanto el encargado ve la utilidad, su corte la trae, y el costo de cada departamento.
        await p.CambiarAsync(p.Encargado, AccionControlada.VerUtilidad, NivelPermiso.Permitido);
        var conUtilidad = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(p.Ruta(p.Encargado)));
        Assert.Equal(utilidad.UtilidadCentavos, conUtilidad.Utilidad?.UtilidadCentavos);
        Assert.Equal(esperados, conUtilidad.PorDepartamento!.Departamentos);
    }

    /// <summary>Lo del piloto con eleventa, sin escribir en él: la prueba aplica su lectura con TurnosDelPiloto.</summary>
    private static (string, string)[] Piloto() =>
    [
        ("Eleventa:Escribir", "true"), ("Eleventa:Activo", "false"), ("Eleventa:Servidor", "127.0.0.1"), ("Eleventa:Puerto", "1"),
        ("Eleventa:Base", @"C:\eleventa\PDVDATA.FDB"), ("Eleventa:Usuario", "SYSDBA"), ("Eleventa:Clave", "masterkey"), ("Eleventa:Caja", "4"),
        ("Eleventa:Cajero", "7"), ("Eleventa:SegundosAlCobrar", "10"), ("Eleventa:SegundosEntreEnvios", "3600"), ("Eleventa:Minutos", "600"),
        ("Eleventa:RespaldoObligatorio", "false"),
    ];

    /// <summary>
    /// En el piloto, el turno que cerró el corte de eleventa ayer sigue en el corte del día de ayer, con lo que se contó allá
    /// («En eleventa»), y no en el de hoy. Sigue sin cerrarse aquí, y el dueño ve su utilidad.
    /// </summary>
    [Fact]
    public async Task En_el_piloto_el_turno_que_corto_eleventa_ayer_sigue_en_el_corte_de_ayer()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Piloto());
        var p = await PuestoAsync(servicio);
        var turno = await AbrirAsync(p, p.Cajero);
        var ayer = Hoy.AddDays(-1);
        var (apertura, corte) = (ayer.ToDateTime(new TimeOnly(8, 0)), ayer.ToDateTime(new TimeOnly(20, 0)));
        // El turno de la caja nueva se abrió ayer y adoptó el de eleventa; se cobró una pluma a mediodía.
        await using (var db = servicio.Base())
        {
            (await db.Turnos.SingleAsync(t => t.Id == turno.Id)).AbiertoUtc = HoraMexico.AUtc(apertura);
            await db.SaveChangesAsync();
            await TurnosDelPiloto.AplicarAsync(db, 4, new DatosDelPiloto { Turnos = [new FilaTurno(31, 4, apertura, null, null)] }, DateTime.UtcNow);
        }
        var ticket = await VenderAsync(p, turno, [new(await VarianteAsync(p, "pluma punto fino"), null, 1000)], new CobroSolicitado(FormaPago.Efectivo, 10000));
        await using (var db = servicio.Base())
        {
            var venta = await db.Ventas.SingleAsync(v => v.Id == ticket.VentaId);
            venta.CreadaUtc = HoraMexico.AUtc(ayer.ToDateTime(new TimeOnly(12, 0)));
            db.Entry(venta).Property(v => v.PagadaUtc).CurrentValue = venta.CreadaUtc;
            await db.SaveChangesAsync();
            // Eleventa hizo su corte anoche: contó $500.
            Assert.Equal(1, (await TurnosDelPiloto.AplicarAsync(db, 4, new DatosDelPiloto { Turnos = [new FilaTurno(31, 4, apertura, corte, 500m)] },
                DateTime.UtcNow)).Cortes);
        }

        var deAyer = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(p.Ruta(p.Dueno, ayer)));
        var deHoy = await Leer<CorteDelDiaDto>(await p.Http.GetAsync(p.Ruta(p.Dueno)));

        Assert.Equal((true, true), (deAyer.Piloto, deAyer.DeOtroDia));
        var delPiloto = Assert.Single(deAyer.Turnos);
        Assert.Equal((turno.Id, 1, ticket.TotalCentavos), (delPiloto.Id, delPiloto.Tickets, delPiloto.VendidoCentavos));
        Assert.Equal(("Corte en eleventa: contado $500.00", (long?)null, (long?)null),
            (delPiloto.CorteEnEleventa, delPiloto.ContadoCentavos, delPiloto.DiferenciaCentavos));
        Assert.Null(deAyer.Cierre);
        Assert.NotNull(deAyer.Utilidad);
        Assert.Equal(ticket.TotalCentavos, deAyer.PorDepartamento!.TotalCentavos);
        Assert.Empty(deHoy.Turnos);
        Assert.Equal(0, deHoy.Tickets);

        // Ya sin el piloto, ayer sigue siendo un día del piloto, con lo contado en eleventa; hoy ya no.
        await using var sinPiloto = servicio.Fabrica.WithWebHostBuilder(b => b.UseSetting("Eleventa:Escribir", "false"));
        var http = sinPiloto.CreateClient();
        var ayerSinPiloto = await Leer<CorteDelDiaDto>(await http.GetAsync(p.Ruta(p.Dueno, ayer)));
        var hoySinPiloto = await Leer<CorteDelDiaDto>(await http.GetAsync(p.Ruta(p.Dueno)));
        Assert.True(ayerSinPiloto.Piloto);
        Assert.Equal("Corte en eleventa: contado $500.00", Assert.Single(ayerSinPiloto.Turnos).CorteEnEleventa);
        Assert.False(hoySinPiloto.Piloto);
    }

    /// <summary>
    /// El recordatorio de cerrar el día: desde la hora de cierre de Configuración › Tienda (21:00 si no se cambia) y solo a
    /// quien tiene «Ver y cerrar el corte del día»; se va al cerrar el día y vuelve si se abre otro turno ese día. Sin hora de
    /// cierre o en el piloto, no sale.
    /// </summary>
    [Fact]
    public async Task El_recordatorio_sale_a_la_hora_de_cierre_a_quien_cierra_el_dia_hasta_que_se_cierra()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var p = await PuestoAsync(servicio);
        var turno = await AbrirAsync(p, p.Cajero);
        var hoy = Hoy;
        DateTime A(int hora, int minuto) => HoraMexico.AUtc(hoy.ToDateTime(new TimeOnly(hora, minuto)));
        async Task<RecordatorioCierreDto?> RecordatorioAsync(SesionDto quien, DateTime ahoraUtc, bool piloto = false)
        {
            await using var db = servicio.Base();
            return await new RegistroCorteDelDia(db).RecordatorioAsync(p.Caja.SucursalId, quien.UsuarioId, ahoraUtc, piloto);
        }

        // A las 21:00 (la de siempre), al dueño; antes no, y a quien no cierra el día, nunca.
        Assert.Null(await RecordatorioAsync(p.Dueno, A(20, 59)));
        Assert.Equal(new RecordatorioCierreDto(hoy, new TimeOnly(21, 0)), await RecordatorioAsync(p.Dueno, A(21, 0)));
        Assert.Null(await RecordatorioAsync(p.Encargado, A(21, 30)));
        Assert.Null(await RecordatorioAsync(p.Cajero, A(21, 30)));
        // En el piloto el día se cierra con el corte de eleventa: no se recuerda aquí.
        Assert.Null(await RecordatorioAsync(p.Dueno, A(21, 30), piloto: true));

        // Con la hora de Configuración › Tienda; y si el encargado tiene el permiso, a él también.
        var configuracion = await p.Http.GetFromJsonAsync<ConfiguracionSucursalDto>($"api/sucursales/{p.Caja.SucursalId}/configuracion");
        await Leer<ConfiguracionSucursalDto>(await p.Http.PutAsJsonAsync($"api/cajas/{p.Caja.Id}/configuracion",
            new SolicitudConfiguracionSucursal(p.Dueno.UsuarioId, configuracion! with { HoraCierre = new TimeOnly(20, 0) })));
        await p.CambiarAsync(p.Encargado, AccionControlada.CorteDelDia, NivelPermiso.Permitido);
        Assert.Null(await RecordatorioAsync(p.Dueno, A(19, 59)));
        Assert.Equal(new TimeOnly(20, 0), (await RecordatorioAsync(p.Dueno, A(20, 30)))?.HoraCierre);
        Assert.NotNull(await RecordatorioAsync(p.Encargado, A(20, 30)));

        // Se cortó el turno y se cerró el día: ya no. Si se abre otro turno, otra vez.
        await Leer<CorteDto>(await CortarAsync(p, turno, p.Encargado, 50000));
        await Leer<CierreDelDiaDto>(await p.CerrarAsync());
        Assert.Null(await RecordatorioAsync(p.Dueno, A(21, 30)));
        await AbrirAsync(p, p.Cajero);
        Assert.NotNull(await RecordatorioAsync(p.Dueno, A(21, 30)));

        // Sin hora de cierre (las 00:00 no pueden serlo: va después de la de apertura), no hay recordatorio.
        await using (var db = servicio.Base())
        {
            var guardada = await db.ConfiguracionesSucursal.SingleAsync(c => c.SucursalId == p.Caja.SucursalId);
            (guardada.HoraApertura, guardada.HoraCierre) = (TimeOnly.MinValue, TimeOnly.MinValue);
            await db.SaveChangesAsync();
        }
        Assert.Null(await RecordatorioAsync(p.Dueno, A(23, 59)));
    }

    /// <summary>La API del recordatorio usa la hora de los trabajos de fondo (la prueba la fija): 204 si no toca.</summary>
    [Fact]
    public async Task La_api_del_recordatorio_contesta_con_la_hora_del_servicio()
    {
        var reloj = new RelojFijo(new DateTimeOffset(HoraMexico.AUtc(Hoy.ToDateTime(new TimeOnly(21, 30))), TimeSpan.Zero));
        await using var servicio = await ServicioDePrueba.CrearAsync(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(reloj);
        });
        var p = await PuestoAsync(servicio);
        await AbrirAsync(p, p.Cajero);
        Task<HttpResponseMessage> Pedir(SesionDto quien) => p.Http.GetAsync($"api/cajas/{p.Caja.Id}/corte-del-dia/recordatorio?usuarioId={quien.UsuarioId}");

        var delDueno = await Leer<RecordatorioCierreDto>(await Pedir(p.Dueno));
        var delCajero = await Pedir(p.Cajero);
        reloj.Ahora = new DateTimeOffset(HoraMexico.AUtc(Hoy.ToDateTime(new TimeOnly(9, 0))), TimeSpan.Zero);
        var enLaManana = await Pedir(p.Dueno);

        Assert.Equal(new RecordatorioCierreDto(Hoy, new TimeOnly(21, 0)), delDueno);
        Assert.Equal(HttpStatusCode.NoContent, delCajero.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, enLaManana.StatusCode);
    }
}
