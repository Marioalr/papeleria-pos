using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El modo multicaja con el puente (2026-11-09; docs/migracion-eleventa.md, «El modo multicaja con el puente»): un solo servicio, en la
/// caja principal, y cada caja del POS escribe en eleventa como SU caja de allá (<c>Eleventa:Cajas</c>, por la serie), en su turno:
/// lo adopta y toma su corte sin tocar el de la otra, y lo que una no puede mandar no detiene lo de la otra.
/// </summary>
public sealed class MulticajaConElPuenteTests
{
    /// <summary>La caja 2 de la tienda, que es la que vende en el piloto (aquí, la caja A del POS); la principal de eleventa es la 1 (la B).</summary>
    private const int CajaDos = 4;
    private const int Principal = 1;

    private static (string, string)[] Ajustes(ServidorEleventa? e = null, bool leer = false, params (string, string)[] mas) =>
    [
        ("Eleventa:Escribir", "true"), ("Eleventa:Activo", leer ? "true" : "false"),
        ("Eleventa:Servidor", e?.Servidor ?? "127.0.0.1"), ("Eleventa:Puerto", (e?.Puerto ?? 1).ToString(CultureInfo.InvariantCulture)),
        ("Eleventa:Base", e?.Base ?? @"C:\eleventa\PDVDATA.FDB"), ("Eleventa:Usuario", "SYSDBA"),
        ("Eleventa:Clave", Environment.GetEnvironmentVariable("PRUEBAS_FIREBIRD_CLAVE") is { Length: > 0 } clave ? clave : "masterkey"),
        ("Eleventa:Cajero", "7"), ("Eleventa:SegundosAlCobrar", "10"),
        ("Eleventa:SegundosEntreEnvios", "3600"), ("Eleventa:Minutos", "600"), ("Eleventa:RespaldoObligatorio", "false"),
        .. mas,
    ];

    /// <summary>Cada caja del POS con la suya: la A como la caja 2 de eleventa y la B como la principal.</summary>
    private static readonly (string, string)[] CadaUnaLaSuya = [("Eleventa:Cajas:A:Caja", "4"), ("Eleventa:Cajas:b:Caja", "1")];

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private sealed record CajaAbierta(HttpClient Http, CajaDto Datos, SesionDto Sesion, TurnoDto Turno, Guid Variante);

    /// <summary>Las dos cajas del POS (la A que ya trae la base y una B nueva), cada una con su turno abierto.</summary>
    private static async Task<(CajaAbierta A, CajaAbierta B)> AbrirLasDosAsync(ServicioDePrueba servicio, Guid? variante = null)
    {
        var http = servicio.Fabrica.CreateClient();
        var a = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        Assert.Equal("A", a.Serie);
        await using (var db = servicio.Base())
        {
            db.Cajas.Add(new Caja { SucursalId = a.SucursalId, Nombre = "Caja principal", Serie = "B" });
            await db.SaveChangesAsync();
        }
        var b = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single(c => c.Serie == "B");
        var sesion = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var encargado = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));
        variante ??= (await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q=pluma%20punto%20fino&sucursalId={a.SucursalId}"))!
            .First(r => r.Codigo is not null).VarianteId;
        var turnoA = await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(a.Id, sesion.UsuarioId, 50000)));
        var turnoB = await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(b.Id, encargado.UsuarioId, 30000)));
        return (new CajaAbierta(http, a, sesion, turnoA, variante.Value), new CajaAbierta(http, b, encargado, turnoB, variante.Value));
    }

    private static async Task<TicketDto> CobrarAsync(CajaAbierta c) =>
        await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, c.Turno.Id, c.Sesion.UsuarioId,
            [new LineaSolicitada(c.Variante, null, 1000)], [new CobroSolicitado(FormaPago.Efectivo, 100000)])));

    private static async Task<EstadoEnviosEleventaDto> EnviosAsync(CajaAbierta c) => (await c.Http.GetFromJsonAsync<EstadoEnviosEleventaDto>("api/eleventa/envios"))!;

    private static async Task<T> EsperarAsync<T>(Func<Task<T>> leer, Func<T, bool> listo)
    {
        var limite = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            var valor = await leer();
            if (listo(valor) || DateTime.UtcNow > limite)
                return valor;
            await Task.Delay(100);
        }
    }

    private static DateTime HoraLocal(int minutosAtras)
    {
        var hora = HoraMexico.ALocal(DateTime.UtcNow).AddMinutes(-minutosAtras);
        return new DateTime(hora.Ticks - hora.Ticks % TimeSpan.TicksPerSecond);
    }

    private static async Task<Guid?> TurnoAbiertoAsync(CajaAbierta c)
    {
        var r = await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/turno");
        return r.StatusCode == HttpStatusCode.NoContent ? null : (await Leer<TurnoDto>(r)).Id;
    }

    // ── La configuración ──

    [Fact]
    public void Cada_caja_del_pos_toma_su_caja_de_eleventa_por_su_serie_y_las_demas_la_de_siempre()
    {
        var opciones = new OpcionesEleventa
        {
            Escribir = true, Servidor = "127.0.0.1", Base = @"C:\eleventa\PDVDATA.FDB", Clave = "x", Caja = 3, Cajero = 7,
            Cajas = { ["B"] = new CajaDelPuente { Caja = 1 }, ["C"] = new CajaDelPuente { Caja = 5, Cajero = 9 } },
        };

        Assert.Equal((3, 7), opciones.DeLaSerie("A"));
        Assert.Equal((1, 7), opciones.DeLaSerie("b"));
        Assert.Equal((5, 9), opciones.DeLaSerie("C"));
        Assert.Equal((3, 7), opciones.DeLaSerie(null));
        Assert.Equal([1, 3, 5], opciones.CajasDeEleventa());
        Assert.Equal(1, opciones.ParaEscribir("B")!.Value.Caja.CajaId);
        Assert.Equal(3, opciones.ParaEscribir()!.Value.Caja.CajaId);
        Assert.Null(opciones.ProblemaDeLasCajas());
    }

    [Fact]
    public void Sin_la_caja_de_siempre_solo_escriben_las_que_tienen_la_suya_y_una_a_medias_no_toma_la_de_otra()
    {
        var opciones = new OpcionesEleventa
        {
            Escribir = true, Servidor = "127.0.0.1", Base = @"C:\eleventa\PDVDATA.FDB", Clave = "x", Cajero = 7,
            Cajas = { ["A"] = new CajaDelPuente { Caja = 3 }, ["B"] = new CajaDelPuente() },
        };

        Assert.Equal((3, 7), opciones.DeLaSerie("A"));
        // La B está, pero sin su número: no escribe (ni como la de siempre, si la hubiera). El celular (CEL) tampoco: no hay de siempre.
        Assert.Null(opciones.DeLaSerie("B"));
        Assert.Null(opciones.DeLaSerie("CEL"));
        Assert.NotNull(opciones.ServidorParaEscribir());
        Assert.Contains("«B»", opciones.ProblemaDeLasCajas());

        opciones.Caja = 3;
        Assert.Null(opciones.DeLaSerie("B"));
        opciones.Cajas["B"].Caja = 3;
        Assert.Contains("la misma caja de eleventa (3)", opciones.ProblemaDeLasCajas());
        // Como antes: sin Cajas, todas con la de siempre; sin ninguna, no se escribe.
        Assert.Null(new OpcionesEleventa { Escribir = true, Servidor = "127.0.0.1", Base = "x", Clave = "x", Cajero = 7 }.ServidorParaEscribir());
    }

    // ── Sin Firebird: lo que la lectura aplica, con filas de eleventa hechas a mano ──

    [Fact]
    public async Task Cada_caja_adopta_el_turno_de_su_caja_de_eleventa_y_el_corte_de_una_no_cierra_el_de_la_otra()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(mas: CadaUnaLaSuya));
        var (a, b) = await AbrirLasDosAsync(servicio);
        var apertura = HoraLocal(120);
        var opciones = servicio.Fabrica.Services.GetRequiredService<OpcionesEleventa>();

        await using (var db = servicio.Base())
        {
            var cajas = await opciones.DelPilotoAsync(db);
            Assert.Equal((CajaDos, Principal), (cajas.De(a.Datos.Id), cajas.De(b.Datos.Id)));
            Assert.Equal([Principal, CajaDos], (await TurnosDelPiloto.ConsultaAsync(db, cajas)).Cajas);
            var adopcion = await TurnosDelPiloto.AplicarAsync(db, cajas, new DatosDelPiloto
            {
                Turnos = [new FilaTurno(31, CajaDos, apertura, null, null), new FilaTurno(32, Principal, apertura, null, null)],
            }, DateTime.UtcNow);
            Assert.Equal((2, 0), (adopcion.Adoptados, adopcion.Cortes));
            Assert.Empty(adopcion.Avisos);
            Assert.Equal(31, (await db.Set<TurnoEnEleventa>().SingleAsync(t => t.TurnoId == a.Turno.Id)).TurnoEleventa);
            Assert.Equal(32, (await db.Set<TurnoEnEleventa>().SingleAsync(t => t.TurnoId == b.Turno.Id)).TurnoEleventa);
        }

        // Eleventa corta el turno de la caja 2: aquí se cierra el de la caja A y el de la B sigue.
        await using (var db = servicio.Base())
        {
            var corte = await TurnosDelPiloto.AplicarAsync(db, await opciones.DelPilotoAsync(db), new DatosDelPiloto
            {
                Turnos = [new FilaTurno(31, CajaDos, apertura, HoraLocal(-1), 500m), new FilaTurno(32, Principal, apertura, null, null)],
            }, DateTime.UtcNow);
            Assert.Equal(1, corte.Cortes);
        }
        Assert.Null(await TurnoAbiertoAsync(a));
        Assert.Equal(b.Turno.Id, await TurnoAbiertoAsync(b));
    }

    [Fact]
    public async Task Una_caja_sin_su_caja_de_eleventa_no_adopta_el_turno_de_otra_y_dos_abiertos_en_una_no_detienen_a_la_otra()
    {
        // Solo la A tiene la suya, y no hay «de siempre»: la B (la principal, que en el piloto todavía no vende con el POS) no adopta nada.
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(mas: ("Eleventa:Cajas:A:Caja", "4")));
        var (a, b) = await AbrirLasDosAsync(servicio);
        var opciones = servicio.Fabrica.Services.GetRequiredService<OpcionesEleventa>();

        await using (var db = servicio.Base())
        {
            var cajas = await opciones.DelPilotoAsync(db);
            Assert.Equal((CajaDos, (int?)null), (cajas.De(a.Datos.Id), cajas.De(b.Datos.Id)));
            Assert.Equal([CajaDos], cajas.Todas);
            var adopcion = await TurnosDelPiloto.AplicarAsync(db, cajas, new DatosDelPiloto
            {
                Turnos = [new FilaTurno(31, CajaDos, HoraLocal(60), null, null), new FilaTurno(32, Principal, HoraLocal(60), null, null)],
            }, DateTime.UtcNow);
            Assert.Equal(1, adopcion.Adoptados);
            Assert.False(await db.Set<TurnoEnEleventa>().AnyAsync(t => t.TurnoId == b.Turno.Id));
        }

        // Con las dos configuradas: dos turnos abiertos en la caja 2 de eleventa avisan y no adoptan; la principal sí adopta el suyo.
        await using (var db = servicio.Base())
        {
            db.Set<TurnoEnEleventa>().RemoveRange(await db.Set<TurnoEnEleventa>().ToListAsync());
            await db.SaveChangesAsync();
            var cajas = new CajasDelPiloto(null, new Dictionary<Guid, int> { [a.Datos.Id] = CajaDos, [b.Datos.Id] = Principal });
            var adopcion = await TurnosDelPiloto.AplicarAsync(db, cajas, new DatosDelPiloto
            {
                Turnos =
                [
                    new FilaTurno(31, CajaDos, HoraLocal(60), null, null), new FilaTurno(33, CajaDos, HoraLocal(5), null, null),
                    new FilaTurno(32, Principal, HoraLocal(60), null, null),
                ],
            }, DateTime.UtcNow);
            Assert.Equal(1, adopcion.Adoptados);
            Assert.Equal([TurnosDelPiloto.AvisoDosTurnos], adopcion.Avisos);
            Assert.Equal(32, (await db.Set<TurnoEnEleventa>().SingleAsync()).TurnoEleventa);
        }
    }

    [Fact]
    public async Task Lo_que_cobra_una_caja_sin_su_caja_de_eleventa_espera_en_la_bandeja_y_su_franja_lo_dice()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(mas: [("Eleventa:Cajas:A:Caja", "4"), ("Eleventa:Cajas:B:Caja", "0")]));
        var (a, b) = await AbrirLasDosAsync(servicio);

        await CobrarAsync(b);

        var envios = await EnviosAsync(b);
        var pendiente = Assert.Single(envios.PorAtender);
        Assert.Equal(EstadoEnvioEleventa.Pendiente, pendiente.Estado);
        Assert.Contains("«Caja principal» (serie B)", pendiente.Error);
        Assert.Equal(0, pendiente.Intentos);
        Assert.Contains("«B»", envios.Problema);
        // La franja de esa caja lo dice; la de la que sí tiene la suya, no. Un celular (nunca abrió turno) tampoco.
        Assert.Equal(CajasDelPuente.AvisoSinSuCaja, (await b.Http.GetFromJsonAsync<EstadoServicioDto>($"api/estado?caja={b.Datos.Id}"))!.AvisoEleventa);
        Assert.Null((await a.Http.GetFromJsonAsync<EstadoServicioDto>($"api/estado?caja={a.Datos.Id}"))!.AvisoEleventa);
        await using (var db = servicio.Base())
        {
            var celular = new Caja { SucursalId = a.Datos.SucursalId, Nombre = "Celular de Luis", Serie = "CEL" };
            db.Cajas.Add(celular);
            await db.SaveChangesAsync();
            Assert.Null((await a.Http.GetFromJsonAsync<EstadoServicioDto>($"api/estado?caja={celular.Id}"))!.AvisoEleventa);
        }
    }

    [Fact]
    public async Task El_ticket_que_cobro_otra_caja_no_se_cancela_ni_se_devuelve_aqui()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(mas: CadaUnaLaSuya));
        var (a, b) = await AbrirLasDosAsync(servicio);
        var ticket = await CobrarAsync(a);

        // La ventana de la otra caja lo dice al buscarlo, y no lo ofrece; en la que lo cobró, nada.
        var enLaOtra = await Leer<VentaParaDevolverDto>(await b.Http.GetAsync($"api/sucursales/{b.Datos.SucursalId}/ventas/folio/{ticket.Folio}?cajaId={b.Datos.Id}"));
        Assert.Equal(DevolucionesConEleventa.DeOtraCaja, enLaOtra.NoSeDevuelveEnElPiloto);
        var enLaSuya = await Leer<VentaParaDevolverDto>(await a.Http.GetAsync($"api/sucursales/{a.Datos.SucursalId}/ventas/folio/{ticket.Folio}?cajaId={a.Datos.Id}"));
        Assert.Null(enLaSuya.NoSeDevuelveEnElPiloto);
        // Y aunque la pidiera una caja de antes, el servicio no la hace.
        var cancelacion = await b.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/cancelacion", new SolicitudCancelacion(b.Sesion.UsuarioId, b.Datos.Id, "Se equivocó"));
        Assert.Equal(HttpStatusCode.BadRequest, cancelacion.StatusCode);
        Assert.Equal(DevolucionesConEleventa.DeOtraCaja, (await cancelacion.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
    }

    // ── Contra un Firebird de prueba ──

    private static async Task<EleventaDePrueba> EleventaAsync()
    {
        var e = await EleventaDePrueba.CrearAsync();
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (1, 'Principal', '10.0.0.10')");
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (4, 'Caja 2', '10.0.0.19')");
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, USUARIO) VALUES (7, 'cajera2')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (5, 'Papelería')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (9, @nombre)", ("nombre", EnvioEleventa.DepartamentoComunes));
        await e.ProductoAsync(10, "PLU-FINA", "Pluma punto fino", depto: 5, costo: 4, precio: 9, existencia: 50);
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN) VALUES (41, 1, 1, -1, 'f', @cuando)",
            ("cuando", HoraLocal(120)));
        return e;
    }

    private static async Task<Guid> MapearAsync(ServicioDePrueba servicio)
    {
        await using var db = servicio.Base();
        var variante = await db.Variantes.Where(v => db.Productos.Any(p => p.Id == v.ProductoId && p.Nombre.StartsWith("Pluma punto fino")))
            .OrderBy(v => v.Id).Select(v => v.Id).FirstAsync();
        db.MapeosImportacion.Add(new MapeoImportacion { Origen = SincronizacionEleventa.Origen, Entidad = nameof(Variante), IdOrigen = "10", IdNuevo = variante });
        await db.SaveChangesAsync();
        return variante;
    }

    private static async Task<EstadoEnviosEleventaDto> MandarAsync(CajaAbierta c, int pendientes = 0)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
        return await EsperarAsync(() => EnviosAsync(c), x => x.Pendientes == pendientes && x.PorAtender.Count(e => e.Estado == EstadoEnvioEleventa.Pendiente) == pendientes);
    }

    private static async Task<int?> FolioEleventaAsync(CajaAbierta c, TicketDto ticket) =>
        (await Leer<TicketDto>(await c.Http.GetAsync($"api/ventas/{ticket.VentaId}/ticket"))).FolioEleventa;

    [FactConFirebird]
    public async Task Cada_caja_del_pos_escribe_en_eleventa_como_su_caja_en_su_turno_y_con_folios_seguidos()
    {
        await using var e = await EleventaAsync();
        var turnoDeLaDos = await e.AbrirCajaAsync(CajaDos, cajero: 7, HoraLocal(60), fondo: 300);
        var turnoDeLaPrincipal = await e.AbrirCajaAsync(Principal, cajero: 7, HoraLocal(50), fondo: 500);
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, mas: CadaUnaLaSuya));
        var (a, b) = await AbrirLasDosAsync(servicio, await MapearAsync(servicio));

        var deLaDos = await CobrarAsync(a);
        var deLaPrincipal = await CobrarAsync(b);
        var otraDeLaDos = await CobrarAsync(a);
        await MandarAsync(a);

        var folios = new[] { await FolioEleventaAsync(a, deLaDos), await FolioEleventaAsync(b, deLaPrincipal), await FolioEleventaAsync(a, otraDeLaDos) };
        Assert.Equal([42, 43, 44], folios);
        var enEleventa = await e.FilasAsync("SELECT FOLIO, CAJA_ID, TURNO_ID FROM VENTATICKETS WHERE FOLIO > 41 ORDER BY FOLIO");
        Assert.Equal([(42, CajaDos, turnoDeLaDos), (43, Principal, turnoDeLaPrincipal), (44, CajaDos, turnoDeLaDos)],
            enEleventa.Select(t => (Convert.ToInt32(t["FOLIO"]), Convert.ToInt32(t["CAJA_ID"]), Convert.ToInt32(t["TURNO_ID"]))));
        // Lo primero que mandó cada turno de aquí le dio su turno de eleventa.
        await using var db = servicio.Base();
        Assert.Equal(turnoDeLaDos, (await db.Set<TurnoEnEleventa>().SingleAsync(t => t.TurnoId == a.Turno.Id)).TurnoEleventa);
        Assert.Equal(turnoDeLaPrincipal, (await db.Set<TurnoEnEleventa>().SingleAsync(t => t.TurnoId == b.Turno.Id)).TurnoEleventa);
    }

    [FactConFirebird]
    public async Task Si_eleventa_no_tiene_abierto_el_turno_de_una_caja_lo_suyo_espera_y_lo_de_la_otra_pasa()
    {
        await using var e = await EleventaAsync();
        var turnoDeLaDos = await e.AbrirCajaAsync(CajaDos, cajero: 7, HoraLocal(60), fondo: 300);
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, mas: CadaUnaLaSuya));
        var (a, b) = await AbrirLasDosAsync(servicio, await MapearAsync(servicio));

        // La principal de eleventa está cerrada: lo que cobra la caja B del POS espera. Lo de la A, cobrado después, sí entra.
        var deLaPrincipal = await CobrarAsync(b);
        var deLaDos = await CobrarAsync(a);
        var envios = await MandarAsync(a, pendientes: 1);

        Assert.Equal(42, await FolioEleventaAsync(a, deLaDos));
        Assert.Null(await FolioEleventaAsync(b, deLaPrincipal));
        var espera = Assert.Single(envios.PorAtender, x => x.Estado == EstadoEnvioEleventa.Pendiente);
        Assert.Equal(($"Venta {deLaPrincipal.Folio}", CorteEnElPiloto.AbreLaCajaEnEleventa), (espera.Descripcion, espera.Error));

        // Abren la principal en eleventa: lo que esperaba entra a su turno, como la caja 1.
        var turnoDeLaPrincipal = await e.AbrirCajaAsync(Principal, cajero: 7, HoraLocal(0), fondo: 500);
        await MandarAsync(a);
        var folio = await FolioEleventaAsync(b, deLaPrincipal);
        Assert.Equal(43, folio);
        var fila = Assert.Single(await e.FilasAsync("SELECT CAJA_ID, TURNO_ID FROM VENTATICKETS WHERE FOLIO = 43"));
        Assert.Equal((Principal, turnoDeLaPrincipal), (Convert.ToInt32(fila["CAJA_ID"]), Convert.ToInt32(fila["TURNO_ID"])));
        Assert.Equal(1, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS WHERE TURNO_ID = @t AND FOLIO > 41", ("t", turnoDeLaDos))));
    }

    [FactConFirebird]
    public async Task La_lectura_trae_los_turnos_de_cada_caja_de_eleventa_y_cada_caja_del_pos_toma_el_corte_de_la_suya()
    {
        await using var e = await EleventaAsync();
        var turnoDeLaDos = await e.AbrirCajaAsync(CajaDos, cajero: 7, HoraLocal(60), fondo: 300);
        var turnoDeLaPrincipal = await e.AbrirCajaAsync(Principal, cajero: 7, HoraLocal(50), fondo: 500);
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true, mas: CadaUnaLaSuya));
        var (a, b) = await AbrirLasDosAsync(servicio, await MapearAsync(servicio));

        await LeerAsync(a);
        await using (var db = servicio.Base())
        {
            Assert.Equal(turnoDeLaDos, (await db.Set<TurnoEnEleventa>().SingleAsync(t => t.TurnoId == a.Turno.Id)).TurnoEleventa);
            Assert.Equal(turnoDeLaPrincipal, (await db.Set<TurnoEnEleventa>().SingleAsync(t => t.TurnoId == b.Turno.Id)).TurnoEleventa);
        }

        // La principal hace su corte en eleventa: se cierra el turno de la caja B del POS; el de la A sigue vendiendo.
        await e.CortarAsync(turnoDeLaPrincipal, HoraLocal(0).AddSeconds(-1));
        await LeerAsync(a);

        Assert.Null(await TurnoAbiertoAsync(b));
        Assert.Equal(a.Turno.Id, await TurnoAbiertoAsync(a));
        await CobrarAsync(a);
        await MandarAsync(a);
        Assert.Equal(turnoDeLaDos, Convert.ToInt32(await e.ValorAsync("SELECT TURNO_ID FROM VENTATICKETS WHERE FOLIO = 42")));
    }

    private static async Task LeerAsync(CajaAbierta c)
    {
        var antes = (await c.Http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa"))!.Lecturas;
        Assert.Equal(HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/leer", null)).StatusCode);
        var despues = await EsperarAsync(async () => (await c.Http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa"))!, x => x.Lecturas > antes);
        Assert.True(despues.Lecturas > antes && despues.Bien, despues.Error);
    }
}
