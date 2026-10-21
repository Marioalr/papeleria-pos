using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Las ventas de otros días y el historial de cortes (paridad con eleventa, 2026-10-17): solo se leen, un día a la vez, con
/// el usuario que entró a la caja. Las de hoy de esa caja las ve cualquiera, como antes; las de otro día o de otra caja,
/// quien tiene «Ver reportes de ventas»; el historial, quien hace el corte de un turno o ve el del día. Un folio con su serie
/// se busca en cualquier día y caja; solo el número, sin los reportes, en la caja desde la que se busca. Cada prueba con su
/// propia base (con la Caja 2): las ventas se mueven de día y los turnos se cierran.
/// </summary>
public sealed class OtrosDiasApiTests : IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-local-{Guid.NewGuid():N}.db");
    private Contingencia? _contingencia;

    /// <summary>
    /// La caja de la prueba sin conexión se cierra (suelta su base) y su base se borra. Solo las conexiones de esa base: las de
    /// todas (<c>ClearAllPools</c>) le cerraban la suya a otra prueba que corría a la par.
    /// </summary>
    public void Dispose()
    {
        _contingencia?.Cerrar(TimeSpan.FromSeconds(20));
        ArchivosDePrueba.BorrarBase(_baseLocal);
    }

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<ErrorDto> RechazoAsync(HttpResponseMessage r, HttpStatusCode codigo)
    {
        Assert.Equal(codigo, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!;
    }

    /// <summary>La tienda de la prueba: la Caja 1 y la Caja 2 (serie B), el cajero, el encargado y el dueño.</summary>
    private sealed record Tienda(HttpClient Http, CajaDto Caja1, CajaDto Caja2, SesionDto Cajero, SesionDto Encargado, SesionDto Dueno);

    private static async Task<Tienda> TiendaAsync(ServicioDePrueba servicio)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja1 = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        await using (var db = servicio.Base())
        {
            db.Cajas.Add(new Caja { SucursalId = caja1.SucursalId, Nombre = "Caja 2", Serie = "B" });
            await db.SaveChangesAsync();
        }
        var caja2 = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single(c => c.Serie == "B");
        async Task<SesionDto> Entrar(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        return new Tienda(http, caja1, caja2, await Entrar("cajero", "1111"), await Entrar("encargado", "2222"), await Entrar("dueno", "3333"));
    }

    private static async Task<TurnoDto> AbrirAsync(HttpClient http, CajaDto caja, SesionDto cajero, long fondo = 50000) =>
        await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, fondo)));

    /// <summary>Artículos comunes por esos precios, pagados como se diga (sin cobros: en efectivo exacto).</summary>
    private static async Task<TicketDto> VenderAsync(HttpClient http, CajaDto caja, TurnoDto turno, long[] precios, params CobroSolicitado[] cobros) =>
        await Leer<TicketDto>(await http.PostAsJsonAsync("api/ventas", new SolicitudCobro(caja.Id, turno.Id, turno.CajeroId,
            precios.Select((p, i) => new LineaSolicitada(null, null, 1000, $"Recuerdo {i + 1}", p)).ToList(),
            cobros.Length > 0 ? cobros : [new CobroSolicitado(FormaPago.Efectivo, precios.Sum())])));

    private static string Ventas(Tienda t, SesionDto quien, string filtros = "", CajaDto? desde = null) =>
        $"api/cajas/{(desde ?? t.Caja1).Id}/ventas?usuarioId={quien.UsuarioId}{filtros}";

    private static string Cortes(Tienda t, SesionDto quien, string filtros = "") => $"api/cajas/{t.Caja1.Id}/cortes?usuarioId={quien.UsuarioId}{filtros}";

    private static string Dia(DateOnly dia) => $"&fecha={dia:yyyy-MM-dd}";

    /// <summary>Mueve una venta (y lo que se devolvió de ella) <paramref name="dias"/> días atrás.</summary>
    private static async Task MoverVentaAsync(ServicioDePrueba servicio, Guid ventaId, int dias)
    {
        await using var db = servicio.Base();
        var pagada = db.Entry((await db.Ventas.FindAsync(ventaId))!).Property(v => v.PagadaUtc);
        pagada.CurrentValue = pagada.CurrentValue!.Value.AddDays(-dias);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Las_de_hoy_de_esta_caja_las_ve_cualquiera_y_las_de_otro_dia_u_otra_caja_quien_ve_los_reportes()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var t = await TiendaAsync(servicio);
        var enCaja1 = await AbrirAsync(t.Http, t.Caja1, t.Cajero);
        var deAyer = await VenderAsync(t.Http, t.Caja1, enCaja1, [2500]);
        var conTarjeta = await VenderAsync(t.Http, t.Caja1, enCaja1, [1000], new CobroSolicitado(FormaPago.Tarjeta, 1000, "Aut 1"));
        var enCaja2 = await AbrirAsync(t.Http, t.Caja2, t.Encargado);
        var deLaCaja2 = await VenderAsync(t.Http, t.Caja2, enCaja2, [4000]);
        await MoverVentaAsync(servicio, deAyer.VentaId!.Value, 1);

        // El cajero, desde la Caja 1: las de hoy de su caja, como antes (sin fecha, la de hoy de la caja principal).
        var deHoy = await Leer<VentasDeUnDiaDto>(await t.Http.GetAsync(Ventas(t, t.Cajero, $"&caja={t.Caja1.Id}")));
        var hoy = deHoy.Hoy;
        var ayer = hoy.AddDays(-1);
        // Lo demás, con «Ver reportes de ventas» (al migrar, solo el dueño): el servicio lo revisa aunque la caja no lo ofrezca.
        var otroDia = await RechazoAsync(await t.Http.GetAsync(Ventas(t, t.Cajero, $"&caja={t.Caja1.Id}{Dia(ayer)}")), HttpStatusCode.Forbidden);
        var otraCaja = await RechazoAsync(await t.Http.GetAsync(Ventas(t, t.Cajero, $"&caja={t.Caja2.Id}")), HttpStatusCode.Forbidden);
        var todas = await RechazoAsync(await t.Http.GetAsync(Ventas(t, t.Cajero)), HttpStatusCode.Forbidden);
        var delEncargado = await RechazoAsync(await t.Http.GetAsync(Ventas(t, t.Encargado, Dia(ayer))), HttpStatusCode.Forbidden);

        Assert.Equal(hoy, deHoy.Fecha);
        var venta = Assert.Single(deHoy.Ventas);
        Assert.Equal((conTarjeta.Folio, null, "Caja 1", "Cajero de prueba", 1000L, 1), (venta.Folio, venta.FolioEleventa, venta.Caja, venta.Cajero,
            venta.TotalCentavos, venta.Articulos));
        Assert.Equal([FormaPago.Tarjeta], venta.Formas);
        Assert.Equal((false, 0L, false), (venta.Cancelada, venta.DevueltoCentavos, venta.DevueltaCompleta));
        Assert.Equal(["Caja 1", "Caja 2"], deHoy.Cajas.Select(c => c.Nombre));
        Assert.Equal(["Cajero de prueba"], deHoy.Cajeros.Select(c => c.Nombre));
        foreach (var rechazo in new[] { otroDia, otraCaja, todas, delEncargado })
        {
            Assert.Equal("no-permitido", rechazo.Codigo);
            Assert.Equal("Las ventas de otro día o de otra caja las ve quien tiene «Ver reportes de ventas»: Dueño de prueba.", rechazo.Mensaje);
        }

        // El dueño: las de ayer de la Caja 1; las de hoy de todas las cajas, las más nuevas primero; y las de un cajero.
        var deAyerDueno = await Leer<VentasDeUnDiaDto>(await t.Http.GetAsync(Ventas(t, t.Dueno, $"&caja={t.Caja1.Id}{Dia(ayer)}")));
        var deTodas = await Leer<VentasDeUnDiaDto>(await t.Http.GetAsync(Ventas(t, t.Dueno)));
        var delCajero = await Leer<VentasDeUnDiaDto>(await t.Http.GetAsync(Ventas(t, t.Dueno, $"&cajero={t.Encargado.UsuarioId}")));
        // Desde la Caja 2, las de hoy de la Caja 2 son «las de esta caja».
        var desdeLaCaja2 = await Leer<VentasDeUnDiaDto>(await t.Http.GetAsync(Ventas(t, t.Encargado, $"&caja={t.Caja2.Id}", t.Caja2)));
        var manana = await RechazoAsync(await t.Http.GetAsync(Ventas(t, t.Dueno, Dia(hoy.AddDays(1)))), HttpStatusCode.BadRequest);

        Assert.Equal(ayer, deAyerDueno.Fecha);
        Assert.Equal([deAyer.Folio], deAyerDueno.Ventas.Select(v => v.Folio));
        Assert.Equal([FormaPago.Efectivo], deAyerDueno.Ventas[0].Formas);
        Assert.Equal([deLaCaja2.Folio, conTarjeta.Folio], deTodas.Ventas.Select(v => v.Folio));
        Assert.Equal(["Caja 2", "Caja 1"], deTodas.Ventas.Select(v => v.Caja));
        Assert.Equal(["Cajero de prueba", "Encargado de prueba"], deTodas.Cajeros.Select(c => c.Nombre));
        Assert.Equal([deLaCaja2.Folio], delCajero.Ventas.Select(v => v.Folio));
        // El filtro sigue ofreciendo a los dos: se puede cambiar de cajero sin quitar el filtro.
        Assert.Equal(["Cajero de prueba", "Encargado de prueba"], delCajero.Cajeros.Select(c => c.Nombre));
        Assert.Equal([deLaCaja2.Folio], desdeLaCaja2.Ventas.Select(v => v.Folio));
        Assert.Equal("Ese día todavía no llega: elige hoy o uno de antes.", manana.Mensaje);
    }

    [Fact]
    public async Task Cada_venta_dice_como_se_pago_y_si_se_cancelo_o_se_devolvio_en_parte_o_toda()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var t = await TiendaAsync(servicio);
        // El encargado devuelve y cancela sin pedir autorización.
        var turno = await AbrirAsync(t.Http, t.Caja1, t.Encargado);
        var enParte = await VenderAsync(t.Http, t.Caja1, turno, [2000, 3000]);
        var cancelada = await VenderAsync(t.Http, t.Caja1, turno, [1500]);
        var mixta = await VenderAsync(t.Http, t.Caja1, turno, [5000], new CobroSolicitado(FormaPago.Efectivo, 2000),
            new CobroSolicitado(FormaPago.Tarjeta, 3000, "Aut 2"));
        var toda = await VenderAsync(t.Http, t.Caja1, turno, [1200]);
        async Task<VentaParaDevolverDto> PorFolio(TicketDto ticket) =>
            (await t.Http.GetFromJsonAsync<VentaParaDevolverDto>($"api/sucursales/{t.Caja1.SucursalId}/ventas/folio/{ticket.Folio}"))!;
        var paraDevolver = await PorFolio(enParte);
        await Leer<ComprobanteDevolucionDto>(await t.Http.PostAsJsonAsync($"api/ventas/{paraDevolver.Id}/devoluciones",
            new SolicitudDevolucion(t.Encargado.UsuarioId, t.Caja1.Id, "No le quedó",
                [new LineaDevolucionSolicitada(paraDevolver.Lineas.Single(l => l.Descripcion == "Recuerdo 2").LineaId, 1000)])));
        await Leer<ComprobanteDevolucionDto>(await t.Http.PostAsJsonAsync($"api/ventas/{cancelada.VentaId}/cancelacion",
            new SolicitudCancelacion(t.Encargado.UsuarioId, t.Caja1.Id, "Se equivocó")));
        var todaParaDevolver = await PorFolio(toda);
        await Leer<ComprobanteDevolucionDto>(await t.Http.PostAsJsonAsync($"api/ventas/{todaParaDevolver.Id}/devoluciones",
            new SolicitudDevolucion(t.Encargado.UsuarioId, t.Caja1.Id, "No era", [new LineaDevolucionSolicitada(todaParaDevolver.Lineas[0].LineaId, 1000)])));

        var dia = await Leer<VentasDeUnDiaDto>(await t.Http.GetAsync(Ventas(t, t.Encargado, $"&caja={t.Caja1.Id}")));

        var porFolio = dia.Ventas.ToDictionary(v => v.Folio);
        Assert.Equal([toda.Folio, mixta.Folio, cancelada.Folio, enParte.Folio], dia.Ventas.Select(v => v.Folio));
        Assert.Equal((false, 3000L, false, 2), (porFolio[enParte.Folio].Cancelada, porFolio[enParte.Folio].DevueltoCentavos,
            porFolio[enParte.Folio].DevueltaCompleta, porFolio[enParte.Folio].Articulos));
        Assert.Equal((true, 0L), (porFolio[cancelada.Folio].Cancelada, porFolio[cancelada.Folio].DevueltoCentavos));
        Assert.Equal([FormaPago.Efectivo, FormaPago.Tarjeta], porFolio[mixta.Folio].Formas);
        Assert.Equal((false, 1200L, true), (porFolio[toda.Folio].Cancelada, porFolio[toda.Folio].DevueltoCentavos, porFolio[toda.Folio].DevueltaCompleta));
    }

    [Fact]
    public async Task Un_folio_con_su_serie_sale_de_cualquier_caja_y_solo_el_numero_de_esta_si_no_ve_los_reportes()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var t = await TiendaAsync(servicio);
        var deLaSemana = await VenderAsync(t.Http, t.Caja1, await AbrirAsync(t.Http, t.Caja1, t.Cajero), [2500]);
        var deLaCaja2 = await VenderAsync(t.Http, t.Caja2, await AbrirAsync(t.Http, t.Caja2, t.Encargado), [4000]);
        await MoverVentaAsync(servicio, deLaSemana.VentaId!.Value, 10);
        // El piloto: eleventa le dio a la de la Caja 2 su folio 4521 (el que salió en el ticket).
        await using (var db = servicio.Base())
        {
            db.EnviosAEleventa.Add(new EnvioAEleventa
            {
                Tipo = TipoEnvioEleventa.Venta, VentaId = deLaCaja2.VentaId, Estado = EstadoEnvioEleventa.Enviada, CreadoUtc = DateTime.UtcNow, FolioEleventa = 4521,
            });
            await db.SaveChangesAsync();
        }
        Task<HttpResponseMessage> Buscar(string folio, SesionDto? quien = null, CajaDto? desde = null) =>
            t.Http.GetAsync(Ventas(t, quien ?? t.Cajero, $"&folio={Uri.EscapeDataString(folio)}", desde));

        // El cajero no ve otros días ni otras cajas, pero un folio con su serie sí, de cualquier día y caja: es lo mismo que
        // escribirlo para devolver.
        var deHaceDias = await Leer<VentasDeUnDiaDto>(await Buscar(deLaSemana.Folio));
        var sinCeros = await Leer<VentasDeUnDiaDto>(await Buscar($" {deLaSemana.Folio.ToLowerInvariant().Replace("-00000", "")} "));
        var deOtraCaja = await Leer<VentasDeUnDiaDto>(await Buscar(deLaCaja2.Folio));
        // Solo el número, en las de su caja: si no, recorriendo los números vería las ventas de la otra.
        var soloElNumero = await Leer<VentasDeUnDiaDto>(await Buscar("1"));
        var deEleventa = await Leer<VentasDeUnDiaDto>(await Buscar("4521"));
        // El encargado tampoco ve los reportes: en la Caja 2, el número y el folio de eleventa son los de sus tickets.
        var soloElNumeroEnLaCaja2 = await Leer<VentasDeUnDiaDto>(await Buscar("1", t.Encargado, t.Caja2));
        var deEleventaEnLaCaja2 = await Leer<VentasDeUnDiaDto>(await Buscar("4521", t.Encargado, t.Caja2));
        // Con «Ver reportes de ventas», como antes: solo el número, de cualquier serie, y el de eleventa desde cualquier caja.
        var soloElNumeroDelDueno = await Leer<VentasDeUnDiaDto>(await Buscar("1", t.Dueno));
        var deEleventaDelDueno = await Leer<VentasDeUnDiaDto>(await Buscar("4521", t.Dueno));
        var noHay = await Leer<VentasDeUnDiaDto>(await Buscar("A-999999"));
        var noEsFolio = await RechazoAsync(await Buscar("hola"), HttpStatusCode.BadRequest);

        Assert.Equal("A-000001", deLaSemana.Folio);
        Assert.Equal(deLaSemana.Folio, deHaceDias.Folio);
        var vieja = Assert.Single(deHaceDias.Ventas);
        Assert.Equal(deHaceDias.Hoy.AddDays(-10), DateOnly.FromDateTime(vieja.FechaLocal));
        // Ya pasaron sus 7 días: no se devuelve (la de hoy sí).
        Assert.False(vieja.SeDevuelve);
        Assert.True(deOtraCaja.Ventas[0].SeDevuelve);
        Assert.Equal("A1", sinCeros.Folio);
        Assert.Equal([deLaSemana.Folio], sinCeros.Ventas.Select(v => v.Folio));
        Assert.Equal(("Caja 2", "Encargado de prueba"), (Assert.Single(deOtraCaja.Ventas).Caja, deOtraCaja.Ventas[0].Cajero));
        Assert.False(deOtraCaja.FolioSoloDeEstaCaja);
        // Solo el número, sin los reportes: A-000001 desde la Caja 1 y B-000001 desde la Caja 2; el 4521 es de la Caja 2.
        Assert.Equal([deLaSemana.Folio], soloElNumero.Ventas.Select(v => v.Folio));
        Assert.Empty(deEleventa.Ventas);
        Assert.Equal(("1", true, "4521", true), (soloElNumero.Folio, soloElNumero.FolioSoloDeEstaCaja, deEleventa.Folio, deEleventa.FolioSoloDeEstaCaja));
        Assert.Equal([deLaCaja2.Folio], soloElNumeroEnLaCaja2.Ventas.Select(v => v.Folio));
        var conEleventa = Assert.Single(deEleventaEnLaCaja2.Ventas);
        Assert.Equal((deLaCaja2.Folio, 4521), (conEleventa.Folio, conEleventa.FolioEleventa));
        // Con los reportes: el de cualquier serie (A-000001 y B-000001), lo más nuevo primero.
        Assert.Equal([deLaCaja2.Folio, deLaSemana.Folio], soloElNumeroDelDueno.Ventas.Select(v => v.Folio));
        Assert.Equal([deLaCaja2.Folio], deEleventaDelDueno.Ventas.Select(v => v.Folio));
        Assert.False(soloElNumeroDelDueno.FolioSoloDeEstaCaja || deEleventaDelDueno.FolioSoloDeEstaCaja);
        Assert.Empty(noHay.Ventas);
        Assert.Equal("A-999999", noHay.Folio);
        Assert.Equal("«hola» no es un folio: escríbelo como sale en el ticket, por ejemplo A-000123.", noEsFolio.Mensaje);
    }

    [Fact]
    public async Task El_historial_de_cortes_trae_lo_esperado_lo_contado_la_diferencia_quien_y_cuando()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var t = await TiendaAsync(servicio);
        // Caja 1: el cajero abre con $500, vende $25, saca $30; el encargado cuenta $480 (esperaba $495: faltan $15).
        var turno = await AbrirAsync(t.Http, t.Caja1, t.Cajero);
        await VenderAsync(t.Http, t.Caja1, turno, [2500]);
        await Leer<ComprobanteMovimientoDto>(await t.Http.PostAsJsonAsync($"api/turnos/{turno.Id}/movimientos",
            new SolicitudMovimiento(TipoMovimientoCaja.Salida, 3000, "Garrafón", t.Cajero.UsuarioId)));
        var corte = await Leer<CorteDto>(await t.Http.PostAsJsonAsync($"api/turnos/{turno.Id}/corte-z", new SolicitudCorteZ(t.Encargado.UsuarioId, 48000)));
        // Caja 2: el turno del encargado sigue abierto.
        var abierto = await AbrirAsync(t.Http, t.Caja2, t.Encargado);

        var delCajero = await RechazoAsync(await t.Http.GetAsync(Cortes(t, t.Cajero)), HttpStatusCode.Forbidden);
        var delEncargado = await Leer<HistorialCortesDto>(await t.Http.GetAsync(Cortes(t, t.Encargado)));
        var delDueno = await Leer<HistorialCortesDto>(await t.Http.GetAsync(Cortes(t, t.Dueno)));
        var deLaCaja2 = await Leer<HistorialCortesDto>(await t.Http.GetAsync(Cortes(t, t.Dueno, $"&caja={t.Caja2.Id}")));
        var delCajeroDeTurno = await Leer<HistorialCortesDto>(await t.Http.GetAsync(Cortes(t, t.Dueno, $"&cajero={t.Cajero.UsuarioId}")));

        // El cajero no lo ve: su corte es a ciegas.
        Assert.Equal("no-permitido", delCajero.Codigo);
        Assert.Equal("El historial de cortes lo ve quien tiene «Hacer el corte de un turno» o «Ver y cerrar el corte del día»: "
            + "Encargado de prueba o Dueño de prueba.", delCajero.Mensaje);
        // El encargado (hace el corte) y el dueño: los dos turnos de hoy, en el orden en que se abrieron.
        Assert.Equal([turno.Id, abierto.Id], delEncargado.Turnos.Select(x => x.Id));
        Assert.Equal(delEncargado.Turnos.Select(x => x.Id), delDueno.Turnos.Select(x => x.Id));
        Assert.False(delEncargado.Piloto);
        var cerrado = delEncargado.Turnos[0];
        Assert.Equal(("Caja 1", "Cajero de prueba", false), (cerrado.Caja, cerrado.Cajero, cerrado.CorteEnEleventa));
        Assert.NotNull(cerrado.CerradoLocal);
        var z = cerrado.Corte!;
        Assert.Equal((TipoCorte.Z, 49500L, 48000L, -1500L), (z.Tipo, z.EsperadoCentavos, z.ContadoCentavos!.Value, z.DiferenciaCentavos!.Value));
        Assert.Equal(("Cajero de prueba", "Encargado de prueba", 1, 50000L, 2500L, 3000L), (z.Cajero, z.RealizadoPor, z.Ventas, z.FondoCentavos,
            z.EfectivoVentasCentavos, z.SalidasCentavos));
        Assert.Equal(corte.FechaLocal, z.FechaLocal);
        var sigueAbierto = delEncargado.Turnos[1];
        Assert.Equal(("Caja 2", "Encargado de prueba", null, false), (sigueAbierto.Caja, sigueAbierto.Cajero, sigueAbierto.Corte, sigueAbierto.CorteEnEleventa));
        Assert.Null(sigueAbierto.CerradoLocal);
        // Los filtros: la caja y el cajero del turno; el de cajero sigue ofreciendo a los dos.
        Assert.Equal([abierto.Id], deLaCaja2.Turnos.Select(x => x.Id));
        Assert.Equal([turno.Id], delCajeroDeTurno.Turnos.Select(x => x.Id));
        Assert.Equal(["Cajero de prueba", "Encargado de prueba"], delCajeroDeTurno.Cajeros.Select(c => c.Nombre));
        Assert.Equal(["Caja 1", "Caja 2"], delEncargado.Cajas.Select(c => c.Nombre));

        // El turno cerrado, tres días antes: sale ese día, y hoy ya no.
        await using (var db = servicio.Base())
        {
            var cerradoEnBase = (await db.Turnos.FindAsync(turno.Id))!;
            cerradoEnBase.AbiertoUtc = cerradoEnBase.AbiertoUtc.AddDays(-3);
            cerradoEnBase.CerradoUtc = cerradoEnBase.CerradoUtc!.Value.AddDays(-3);
            var z3 = await db.Cortes.SingleAsync(c => c.TurnoId == turno.Id && c.Tipo == TipoCorte.Z);
            z3.FechaUtc = z3.FechaUtc.AddDays(-3);
            await db.SaveChangesAsync();
        }
        var hace3 = delEncargado.Hoy.AddDays(-3);
        var deEseDia = await Leer<HistorialCortesDto>(await t.Http.GetAsync(Cortes(t, t.Encargado, Dia(hace3))));
        var deHoy = await Leer<HistorialCortesDto>(await t.Http.GetAsync(Cortes(t, t.Encargado)));
        var manana = await RechazoAsync(await t.Http.GetAsync(Cortes(t, t.Encargado, Dia(delEncargado.Hoy.AddDays(1)))), HttpStatusCode.BadRequest);

        Assert.Equal(hace3, deEseDia.Fecha);
        Assert.Equal((turno.Id, -1500L), (Assert.Single(deEseDia.Turnos).Id, deEseDia.Turnos[0].Corte!.DiferenciaCentavos!.Value));
        Assert.Equal(hace3, DateOnly.FromDateTime(deEseDia.Turnos[0].Corte!.FechaLocal));
        Assert.Equal([abierto.Id], deHoy.Turnos.Select(x => x.Id));
        Assert.Equal("Ese día todavía no llega: elige hoy o uno de antes.", manana.Mensaje);
    }

    /// <summary>
    /// El piloto con eleventa: el corte de la caja se hace allá. Un turno abierto dice «Corte en eleventa», y también uno que
    /// se cerró sin corte aquí (el corte de eleventa lo cierra). Solo para convivir con eleventa.
    /// </summary>
    [Fact]
    public async Task En_el_piloto_el_corte_que_se_hace_en_eleventa_lo_dice()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(
            ("Eleventa:Escribir", "true"), ("Eleventa:Servidor", "127.0.0.1"), ("Eleventa:Puerto", "1"), ("Eleventa:Base", @"C:\eleventa\PDVDATA.FDB"),
            ("Eleventa:Clave", "masterkey"), ("Eleventa:Caja", "4"), ("Eleventa:Cajero", "7"), ("Eleventa:SegundosEntreEnvios", "3600"));
        var t = await TiendaAsync(servicio);
        var cerradoAlla = await AbrirAsync(t.Http, t.Caja2, t.Encargado);
        await using (var db = servicio.Base())
        {
            // Como lo deja el corte de eleventa: el turno cerrado, sin corte Z aquí.
            (await db.Turnos.FindAsync(cerradoAlla.Id))!.CerradoUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        var abierto = await AbrirAsync(t.Http, t.Caja1, t.Cajero);

        var historial = await Leer<HistorialCortesDto>(await t.Http.GetAsync(Cortes(t, t.Dueno)));

        Assert.True(historial.Piloto);
        Assert.Equal([cerradoAlla.Id, abierto.Id], historial.Turnos.Select(x => x.Id));
        Assert.All(historial.Turnos, x => Assert.Equal((null, true), (x.Corte, x.CorteEnEleventa)));
    }

    /// <summary>
    /// Sin la caja principal, la caja solo tiene su copia para vender: las ventas y los cortes de todas las cajas no están
    /// aquí. Lo dice sin esperar (con la contingencia que ya sabe que no contesta) y también si se cae a media consulta.
    /// </summary>
    [Fact]
    public async Task Sin_la_caja_principal_las_ventas_y_los_cortes_dicen_que_esperan_a_que_vuelva()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var caja = (await servicio.Fabrica.CreateClient().GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var interruptor = new Interruptor(servicio.Fabrica.Server.CreateHandler());
        var http = new HttpClient(interruptor);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = caja.Id };
        var contingencia = _contingencia = new Contingencia(new AlmacenLocal(_baseLocal), http, conexion);
        var cliente = new ClienteCaja(http, conexion, contingencia);
        await contingencia.LatidoAsync();
        var dueno = await cliente.EntrarAsync("dueno", "3333");
        Assert.NotNull(await cliente.VentasDeUnDiaAsync(caja.Id, dueno.UsuarioId, null, null, null));

        // Se cae a media consulta: lo dice igual, y la caja ya sabe que no contesta.
        interruptor.Caido = true;
        var aMedias = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.HistorialCortesAsync(caja.Id, dueno.UsuarioId, null, null, null));
        var ventas = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.VentasDeUnDiaAsync(caja.Id, dueno.UsuarioId, null, caja.Id, null));
        var folio = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.VentasDeUnDiaAsync(caja.Id, dueno.UsuarioId, null, null, null, "A-000001"));

        Assert.Equal((ClienteCaja.CortesSinConexion, "sin-conexion"), (aMedias.Message, aMedias.Codigo));
        Assert.False(contingencia.ServidorResponde);
        Assert.Equal((ClienteCaja.VentasSinConexion, "sin-conexion"), (ventas.Message, ventas.Codigo));
        Assert.Equal(ClienteCaja.VentasSinConexion, folio.Message);
        Assert.Contains("solo tiene su copia para vender", ClienteCaja.VentasSinConexion);
    }
}
