using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Impresion;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Interfaz.Componentes;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Catalogo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Control;
using PapeleriaPos.Nucleo.Importacion;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Pruebas.Eleventa;
using PapeleriaPos.Pruebas.Interfaz;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El piloto en la caja 2 (docs/migracion-eleventa.md, «Lo que falta para el piloto»): la caja nueva adopta el turno que
/// eleventa tiene abierto en esa caja y toma su corte (cierra su turno como «corte hecho en eleventa», con lo contado allá);
/// lo que seguía en la bandeja de antes del corte queda por revisar sin detener lo demás; y el folio que eleventa repite en
/// otra caja se reimprime con el de la caja y, al devolver, se elige por la hora y el total.
/// </summary>
public sealed class TurnoDelPilotoTests : IDisposable
{
    private const int CajaEleventa = 4;

    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-local-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var archivo in new[] { _baseLocal, _baseLocal + "-wal", _baseLocal + "-shm" })
            File.Delete(archivo);
    }

    /// <summary>El piloto: escribe en eleventa (y lee, si se da el servidor de prueba). Sin servidor, uno que no contesta.</summary>
    private static (string, string)[] Ajustes(ServidorEleventa? e = null, bool leer = false) =>
    [
        ("Eleventa:Escribir", "true"), ("Eleventa:Activo", leer ? "true" : "false"),
        ("Eleventa:Servidor", e?.Servidor ?? "127.0.0.1"), ("Eleventa:Puerto", (e?.Puerto ?? 1).ToString(CultureInfo.InvariantCulture)),
        ("Eleventa:Base", e?.Base ?? @"C:\eleventa\PDVDATA.FDB"), ("Eleventa:Usuario", "SYSDBA"),
        ("Eleventa:Clave", Environment.GetEnvironmentVariable("PRUEBAS_FIREBIRD_CLAVE") is { Length: > 0 } clave ? clave : "masterkey"),
        ("Eleventa:Caja", CajaEleventa.ToString(CultureInfo.InvariantCulture)), ("Eleventa:Cajero", "7"),
        ("Eleventa:SegundosAlCobrar", "10"),
        // Que en la prueba solo mande el cobro o quien lo pida, y solo lea cuando se le pida.
        ("Eleventa:SegundosEntreEnvios", "3600"), ("Eleventa:Minutos", "600"),
        // Sin respaldo propio de eleventa (lo prueban RespaldoDeEleventaTests y RespaldoDelPilotoTests).
        ("Eleventa:RespaldoObligatorio", "false"),
    ];

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private sealed record Caja(HttpClient Http, CajaDto Datos, SesionDto Sesion, TurnoDto Turno, Guid Variante);

    /// <param name="variante">La pluma que se vende; si no se da, la primera que encuentra la búsqueda.</param>
    private static async Task<Caja> AbrirAsync(ServicioDePrueba servicio, Guid? variante = null)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var sesion = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var turno = await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, sesion.UsuarioId, 50000)));
        variante ??= (await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q=pluma%20punto%20fino&sucursalId={caja.SucursalId}"))!
            .First(r => r.Codigo is not null).VarianteId;
        return new Caja(http, caja, sesion, turno, variante.Value);
    }

    /// <summary>Una pluma (la del producto 10 de eleventa) y, si se pide, un artículo común de $15, en efectivo.</summary>
    private static async Task<TicketDto> CobrarAsync(Caja c, TurnoDto? turno = null, bool comun = false)
    {
        List<LineaSolicitada> lineas = [new(c.Variante, null, 1000)];
        if (comun)
            lineas.Add(new LineaSolicitada(null, null, 1000, "Recuerdo", 1500));
        return await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, (turno ?? c.Turno).Id, c.Sesion.UsuarioId,
            lineas, [new CobroSolicitado(FormaPago.Efectivo, 100000)])));
    }

    private static async Task<EstadoEnviosEleventaDto> EnviosAsync(Caja c) => (await c.Http.GetFromJsonAsync<EstadoEnviosEleventaDto>("api/eleventa/envios"))!;

    /// <summary>
    /// Espera a que el servicio llegue a algo que hace en segundo plano, hasta un minuto: con todas las pruebas a la vez, el
    /// Firebird de prueba tarda en abrir cada conexión.
    /// </summary>
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

    /// <summary>
    /// Una pluma de la caja que vino del producto 10 de eleventa (como lo deja la importación). Va antes de que arranque el
    /// servicio: si lee eleventa, su primera lectura ya la encuentra.
    /// </summary>
    private static async Task<Guid> MapearAsync(ServicioDePrueba servicio)
    {
        await using var db = servicio.Base();
        var variante = await db.Variantes.Where(v => db.Productos.Any(p => p.Id == v.ProductoId && p.Nombre.StartsWith("Pluma punto fino")))
            .OrderBy(v => v.Id).Select(v => v.Id).FirstAsync();
        db.MapeosImportacion.Add(new MapeoImportacion { Origen = SincronizacionEleventa.Origen, Entidad = nameof(Variante), IdOrigen = "10", IdNuevo = variante });
        await db.SaveChangesAsync();
        return variante;
    }

    /// <summary>Que la bandeja mande ya lo pendiente, y esperar a que no quede nada: el cobro no espera si va otro envío.</summary>
    private static async Task<EstadoEnviosEleventaDto> MandarAsync(Caja c)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
        // El estado se lee en dos consultas: se espera a que las dos lo digan.
        return await EsperarAsync(() => EnviosAsync(c), x => x.Pendientes == 0 && x.PorAtender.All(e => e.Estado != EstadoEnvioEleventa.Pendiente));
    }

    /// <summary>El folio de eleventa con que se reimprime ese ticket.</summary>
    private static async Task<int?> FolioEleventaAsync(Caja c, TicketDto ticket) =>
        (await Leer<TicketDto>(await c.Http.GetAsync($"api/ventas/{ticket.VentaId}/ticket"))).FolioEleventa;

    private static DateTime HoraLocal(int minutosAtras)
    {
        var hora = HoraMexico.ALocal(DateTime.UtcNow).AddMinutes(-minutosAtras);
        return new DateTime(hora.Ticks - hora.Ticks % TimeSpan.TicksPerSecond);
    }

    private static async Task<Guid?> TurnoAbiertoAsync(Caja c)
    {
        var r = await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/turno");
        return r.StatusCode == HttpStatusCode.NoContent ? null : (await Leer<TurnoDto>(r)).Id;
    }

    // ── Sin Firebird: lo que la lectura aplica, con filas de eleventa hechas a mano ──

    [Fact]
    public async Task Al_cerrarse_en_eleventa_el_turno_adoptado_la_caja_cierra_el_suyo_con_lo_contado_alla()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio);
        await CobrarAsync(c);
        var apertura = HoraLocal(120);

        // La lectura ve el turno que eleventa tiene abierto en la caja 2: el de la caja nueva lo adopta.
        await using (var db = servicio.Base())
        {
            var adopcion = await TurnosDelPiloto.AplicarAsync(db, CajaEleventa, new DatosDelPiloto { Turnos = [new FilaTurno(31, CajaEleventa, apertura, null, null)] },
                DateTime.UtcNow);
            Assert.Equal((1, 0), (adopcion.Adoptados, adopcion.Cortes));
            Assert.Empty(adopcion.Avisos);
        }
        // Eleventa hace su corte: contó $787.50 y le faltaron $12.50.
        var corte = HoraLocal(-1);
        await using (var db = servicio.Base())
        {
            var resultado = await TurnosDelPiloto.AplicarAsync(db, CajaEleventa, new DatosDelPiloto
            {
                Turnos = [new FilaTurno(31, CajaEleventa, apertura, corte, 787.5m)],
                Cierres = [new FilaCierreDeTurno(31, Falto: true, 12.5m)],
            }, DateTime.UtcNow);
            Assert.Equal(1, resultado.Cortes);
        }

        Assert.Null(await TurnoAbiertoAsync(c));
        await using (var db = servicio.Base())
        {
            var turno = await db.Turnos.SingleAsync(t => t.Id == c.Turno.Id);
            Assert.Equal(HoraMexico.AUtc(corte), turno.CerradoUtc);
            var enEleventa = await db.Set<TurnoEnEleventa>().SingleAsync();
            Assert.Equal((31, 78750L, -1250L), (enEleventa.TurnoEleventa, enEleventa.ContadoCentavos, enEleventa.DiferenciaCentavos));
            Assert.NotNull(enEleventa.TomadoUtc);
            // Sin conteo aquí: ni corte Z ni diferencia de la caja nueva; queda en la bitácora.
            Assert.False(await db.Cortes.AnyAsync(x => x.TurnoId == c.Turno.Id));
            var bitacora = await db.Bitacora.SingleAsync(b => b.Accion == AccionControlada.CorteZ);
            Assert.Contains("hecho en eleventa", bitacora.Detalle);
            Assert.EndsWith("Corte en eleventa: contado $787.50, faltaron $12.50", bitacora.Detalle);
            Assert.Equal(-1250, bitacora.MontoCentavos);
        }
        // El corte del día lo dice aparte: es el cajón de las dos cajas, no va en lo contado ni en las diferencias de aquí.
        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var dia = await Leer<CorteDelDiaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/corte-del-dia?usuarioId={dueno.UsuarioId}"));
        var delDia = Assert.Single(dia.Turnos);
        Assert.Equal(("Corte en eleventa: contado $787.50, faltaron $12.50", (long?)null, (long?)null),
            (delDia.CorteEnEleventa, delDia.ContadoCentavos, delDia.DiferenciaCentavos));

        // Ya se puede abrir otro turno en la caja nueva; adopta el siguiente que abra eleventa.
        var otro = await Leer<TurnoDto>(await c.Http.PostAsJsonAsync("api/turnos", new SolicitudTurno(c.Datos.Id, c.Sesion.UsuarioId, 30000)));
        await using (var db = servicio.Base())
        {
            var siguiente = await TurnosDelPiloto.AplicarAsync(db, CajaEleventa, new DatosDelPiloto
            {
                Turnos = [new FilaTurno(31, CajaEleventa, apertura, corte, 787.5m), new FilaTurno(32, CajaEleventa, HoraLocal(0), null, null)],
            }, DateTime.UtcNow);
            Assert.Equal((1, 0), (siguiente.Adoptados, siguiente.Cortes));
            Assert.Equal(32, (await db.Set<TurnoEnEleventa>().SingleAsync(t => t.TurnoId == otro.Id)).TurnoEleventa);
        }
    }

    [Fact]
    public async Task Si_eleventa_corto_anoche_y_en_la_manana_se_cobro_en_el_turno_viejo_lo_de_hoy_sigue_en_el_corte_del_dia()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio);
        var hoy = HoraMexico.ALocal(DateTime.UtcNow).Date;
        var (aperturaAyer, corteAyer) = (hoy.AddDays(-1).AddHours(8), hoy.AddDays(-1).AddHours(20));
        // El turno de la caja nueva se abrió ayer y adoptó el de eleventa.
        await using (var db = servicio.Base())
        {
            (await db.Turnos.SingleAsync(t => t.Id == c.Turno.Id)).AbiertoUtc = HoraMexico.AUtc(aperturaAyer);
            await db.SaveChangesAsync();
            await TurnosDelPiloto.AplicarAsync(db, CajaEleventa, new DatosDelPiloto { Turnos = [new FilaTurno(31, CajaEleventa, aperturaAyer, null, null)] },
                DateTime.UtcNow);
        }
        // Eleventa cortó anoche con la caja 2 apagada: en la mañana se cobra en ese turno antes de que la lectura vea el corte.
        var ticket = await CobrarAsync(c);
        await using (var db = servicio.Base())
            Assert.Equal(1, (await TurnosDelPiloto.AplicarAsync(db, CajaEleventa,
                new DatosDelPiloto { Turnos = [new FilaTurno(31, CajaEleventa, aperturaAyer, corteAyer, 500m)] }, DateTime.UtcNow)).Cortes);

        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var dia = await Leer<CorteDelDiaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/corte-del-dia?usuarioId={dueno.UsuarioId}"));

        // Lo de hoy sigue en el corte del día de hoy: el turno se cerró con lo último que pasó en él. El corte de eleventa
        // conserva su hora.
        await using var revision = servicio.Base();
        var venta = await revision.Ventas.SingleAsync(v => v.Id == ticket.VentaId);
        Assert.Equal((1, venta.TotalCentavos), (dia.Tickets, dia.VendidoCentavos));
        var delDia = Assert.Single(dia.Turnos);
        Assert.Equal((c.Turno.Id, 1), (delDia.Id, delDia.Tickets));
        Assert.Equal(venta.PagadaUtc, (await revision.Turnos.SingleAsync(t => t.Id == c.Turno.Id)).CerradoUtc);
        Assert.Equal(HoraMexico.AUtc(corteAyer), (await revision.Set<TurnoEnEleventa>().SingleAsync()).CortadoUtc);
    }

    [Fact]
    public async Task Con_dos_turnos_abiertos_en_eleventa_no_adopta_ninguno_y_avisa()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        await AbrirAsync(servicio);
        await using var db = servicio.Base();

        var resultado = await TurnosDelPiloto.AplicarAsync(db, CajaEleventa, new DatosDelPiloto
        {
            Turnos = [new FilaTurno(31, CajaEleventa, HoraLocal(60), null, null), new FilaTurno(32, CajaEleventa, HoraLocal(30), null, null)],
        }, DateTime.UtcNow);

        Assert.Equal(0, resultado.Adoptados);
        Assert.Equal([TurnosDelPiloto.AvisoDosTurnos], resultado.Avisos);
        Assert.False(await db.Set<TurnoEnEleventa>().AnyAsync());
    }

    [Fact]
    public async Task Si_el_turno_adoptado_ya_no_esta_como_estaba_no_toma_su_corte_y_avisa()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio);
        var apertura = HoraLocal(60);
        await using var db = servicio.Base();
        await TurnosDelPiloto.AplicarAsync(db, CajaEleventa, new DatosDelPiloto { Turnos = [new FilaTurno(31, CajaEleventa, apertura, null, null)] }, DateTime.UtcNow);

        // El mismo número, con otra hora de inicio (una restauración de eleventa vuelve a dar los mismos números) y cerrado.
        var resultado = await TurnosDelPiloto.AplicarAsync(db, CajaEleventa, new DatosDelPiloto
        {
            Turnos = [new FilaTurno(31, CajaEleventa, apertura.AddMinutes(5), HoraLocal(1), 100m)],
        }, DateTime.UtcNow);

        Assert.Equal(0, resultado.Cortes);
        Assert.Equal([TurnosDelPiloto.AvisoTurnoNoEsta], resultado.Avisos);
        Assert.Equal(c.Turno.Id, await TurnoAbiertoAsync(c));
    }

    /// <summary>
    /// Una venta de esta caja que ya está en eleventa con el folio 42 (la bandeja la anotó como enviada) y, si se pide, otro
    /// ticket de la caja principal de eleventa con el mismo folio, como lo encuentra la revisión tardía.
    /// </summary>
    private static async Task<(TicketDto Ticket, Guid EnvioId)> EnviadaAsync(ServicioDePrueba servicio, Caja c, bool repetida)
    {
        var ticket = await CobrarAsync(c);
        await using var db = servicio.Base();
        var envio = await db.EnviosAEleventa.SingleAsync(e => e.VentaId == ticket.VentaId);
        (envio.Estado, envio.FolioEleventa, envio.TicketEleventa, envio.EnviadaUtc, envio.FolioRepetido) =
            (EstadoEnvioEleventa.Enviada, 42, 500, DateTime.UtcNow, repetida);
        if (repetida)
            db.Set<FolioRepetidoEnEleventa>().Add(new FolioRepetidoEnEleventa
            {
                EnvioId = envio.Id, Folio = 42, TicketEleventa = 77, Caja = "Principal", VendidoUtc = DateTime.UtcNow.AddMinutes(-3), TotalCentavos = 4550,
                VistoUtc = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        return (ticket, envio.Id);
    }

    [Fact]
    public async Task Un_folio_que_eleventa_repitio_se_reimprime_con_el_de_la_caja_y_el_puente_da_los_dos_tickets()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio);
        var (repetida, _) = await EnviadaAsync(servicio, c, repetida: true);

        var reimpresion = await Leer<TicketDto>(await c.Http.GetAsync($"api/ventas/{repetida.VentaId}/ticket"));
        var folio = await Leer<FolioEleventaDto>(await c.Http.GetAsync("api/eleventa/folio/42"));

        Assert.Equal((null, repetida.Folio), (reimpresion.FolioEleventa, reimpresion.FolioImpreso));
        Assert.Equal(2, folio.Tickets.Count);
        // De la más vieja a la más nueva: el de la principal se cobró antes.
        Assert.Equal(("Principal", 4550L, (string?)null, 77, FoliosRepetidos.SeDevuelveEnEleventa),
            (folio.Tickets[0].Caja, folio.Tickets[0].TotalCentavos, folio.Tickets[0].FolioCaja, folio.Tickets[0].TicketEleventa, folio.Tickets[0].Nota));
        Assert.Equal((c.Datos.Nombre, repetida.TotalCobradoCentavos, repetida.Folio, 500, (string?)null),
            (folio.Tickets[1].Caja, folio.Tickets[1].TotalCentavos, folio.Tickets[1].FolioCaja, folio.Tickets[1].TicketEleventa, folio.Tickets[1].Nota));
        Assert.Empty((await Leer<FolioEleventaDto>(await c.Http.GetAsync("api/eleventa/folio/43"))).Tickets);
    }

    [Fact]
    public async Task Un_folio_de_eleventa_que_no_se_repitio_se_sigue_imprimiendo()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio);
        var (ticket, _) = await EnviadaAsync(servicio, c, repetida: false);

        var reimpresion = await Leer<TicketDto>(await c.Http.GetAsync($"api/ventas/{ticket.VentaId}/ticket"));

        Assert.Equal((42, "42"), (reimpresion.FolioEleventa, reimpresion.FolioImpreso));
        Assert.Single((await Leer<FolioEleventaDto>(await c.Http.GetAsync("api/eleventa/folio/42"))).Tickets);
    }

    [Fact]
    public async Task Al_devolver_con_un_folio_que_eleventa_repitio_se_elige_por_la_hora_y_el_total()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes());
        var c = await AbrirAsync(servicio);
        var (repetida, _) = await EnviadaAsync(servicio, c, repetida: true);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = c.Datos.Id };
        var servicios = new ServiceCollection()
            .AddSingleton(new ClienteCaja(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion))
            .AddSingleton(new ClientePuente(new HttpClient(servicio.Fabrica.Server.CreateHandler()), conexion))
            .AddSingleton<IImpresora>(new SinImpresora())
            .AddSingleton<IJSRuntime>(new PaginaQueContesta())
            .AddScoped<ModuloCaja>()
            .BuildServiceProvider();
        await using var alcance = servicios.CreateAsyncScope();
        var pantalla = new PantallaSinNavegador(alcance.ServiceProvider);
        // La ventana de devoluciones, en el piloto (lo dice la pantalla de venta en cascada).
        await pantalla.AbrirAsync<CascadingValue<bool>>(new()
        {
            ["Name"] = PilotoConEleventa.Cascada, ["Value"] = true,
            ["ChildContent"] = (RenderFragment)(b =>
            {
                b.OpenComponent<DevolverCancelar>(0);
                b.AddAttribute(1, nameof(DevolverCancelar.Caja), c.Datos);
                b.AddAttribute(2, nameof(DevolverCancelar.Sesion), c.Sesion);
                b.CloseComponent();
            }),
        });

        await pantalla.EscribirAsync("folio", "42");
        await pantalla.TeclaAsync("folio", "Enter");
        await pantalla.EsperarAsync(t => t.Contains("lo tienen 2 tickets"), "los dos tickets con el folio 42");

        var texto = PantallaSinNavegador.Limpio(await pantalla.TextoAsync());
        Assert.Contains("El folio 42 lo tienen 2 tickets: elige el del cliente por la hora y el total que dice su ticket.", texto);
        Assert.Contains("Principal", texto);
        Assert.Contains("$45.50", texto);
        // El de la principal se devuelve en eleventa.
        var botones = (await pantalla.LeerAsync()).Elementos.Where(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Es este").ToList();
        Assert.Equal(2, botones.Count);
        await pantalla.OprimirAsync(botones[0]);
        await pantalla.EsperarAsync(t => t.Contains(FoliosRepetidos.SeDevuelveEnEleventa), "que el de la principal se devuelve en eleventa");
        // El de esta caja se abre aquí, con sus artículos.
        botones = (await pantalla.LeerAsync()).Elementos.Where(e => e.Etiqueta == "button" && PantallaSinNavegador.Limpio(e.Texto) == "Es este").ToList();
        await pantalla.OprimirAsync(botones[1]);
        await pantalla.EsperarAsync(t => t.Contains(repetida.Folio) && t.Contains("Devolver lo marcado"), "el ticket de esta caja para devolver");
        Assert.DoesNotContain("lo tienen 2 tickets", await pantalla.TextoAsync());
        Assert.Empty(pantalla.Errores);
    }

    // ── Contra un Firebird de prueba ──

    [FactConFirebird]
    public async Task La_caja_adopta_el_turno_de_eleventa_toma_su_corte_y_lo_que_sigue_va_al_turno_siguiente()
    {
        await using var e = await EleventaAsync();
        var primero = await e.AbrirCajaAsync(CajaEleventa, cajero: 7, HoraLocal(60), fondo: 300);
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor, leer: true));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        // La primera lectura, al arrancar: la caja nueva todavía no tenía turno.
        await EsperarAsync(async () => (await c.Http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa"))!, x => x.Lecturas >= 1);

        // Lo primero que manda el turno de la caja nueva le da su turno de eleventa.
        var ticket = await CobrarAsync(c);
        await MandarAsync(c);
        Assert.NotNull(await FolioEleventaAsync(c, ticket));
        await using (var db = servicio.Base())
            Assert.Equal(primero, (await db.Set<TurnoEnEleventa>().SingleAsync(t => t.TurnoId == c.Turno.Id)).TurnoEleventa);

        // Eleventa hace su corte (cuadró) y la caja nueva lo toma en la siguiente lectura.
        await e.CortarAsync(primero, HoraLocal(0).AddSeconds(-1));
        await e.EjecutarAsync("UPDATE TURNOS SET EFECTIVO_AL_CIERRE = 309 WHERE ID = @id", ("id", primero));
        await LeerAsync(c);

        Assert.Null(await TurnoAbiertoAsync(c));
        await using (var db = servicio.Base())
        {
            var tomado = await db.Set<TurnoEnEleventa>().SingleAsync(t => t.TurnoId == c.Turno.Id);
            Assert.Equal((30900L, (long?)null), (tomado.ContadoCentavos, tomado.DiferenciaCentavos));
        }

        // Otro turno en la caja nueva; eleventa abre el suyo: la siguiente venta va a ese.
        var segundo = await e.AbrirCajaAsync(CajaEleventa, cajero: 7, HoraLocal(0), fondo: 300);
        var otroTurno = await Leer<TurnoDto>(await c.Http.PostAsJsonAsync("api/turnos", new SolicitudTurno(c.Datos.Id, c.Sesion.UsuarioId, 30000)));
        var siguiente = await CobrarAsync(c, otroTurno);
        await MandarAsync(c);

        var folio = await FolioEleventaAsync(c, siguiente);
        Assert.Equal(await FolioEleventaAsync(c, ticket) + 1, folio);
        Assert.Equal(segundo, Convert.ToInt32(await e.ValorAsync("SELECT TURNO_ID FROM VENTATICKETS WHERE FOLIO = @f", ("f", folio))));
        await using (var db = servicio.Base())
            Assert.Equal(segundo, (await db.Set<TurnoEnEleventa>().SingleAsync(t => t.TurnoId == otroTurno.Id)).TurnoEleventa);
    }

    [FactConFirebird]
    public async Task Lo_que_seguia_en_la_bandeja_de_antes_del_corte_queda_por_revisar_y_no_detiene_lo_demas()
    {
        // Sin el departamento de los comunes, la venta con un común no entra (no es la red): se queda en la bandeja.
        await using var e = await EleventaAsync(conComunes: false);
        var primero = await e.AbrirCajaAsync(CajaEleventa, cajero: 7, HoraLocal(60), fondo: 300);
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var atorada = await CobrarAsync(c, comun: true);
        Assert.Equal(1, (await EnviosAsync(c)).Pendientes);
        // Se cobró hace 10 minutos; eleventa hizo su corte hace 5.
        await using (var db = servicio.Base())
        {
            var pagada = db.Entry((await db.Ventas.FindAsync(atorada.VentaId))!).Property(v => v.PagadaUtc);
            pagada.CurrentValue = pagada.CurrentValue!.Value.AddMinutes(-10);
            await db.SaveChangesAsync();
        }
        await e.CortarAsync(primero, HoraLocal(5));

        // Ya con el departamento, eleventa abre otro turno y la caja nueva vende otra vez.
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (9, @nombre)", ("nombre", EnvioEleventa.DepartamentoComunes));
        var segundo = await e.AbrirCajaAsync(CajaEleventa, cajero: 7, HoraLocal(1), fondo: 300);
        await CobrarAsync(c);
        var envios = await MandarAsync(c);

        // La de antes del corte ya se contó allá: queda por revisar, con el porqué. La de después entró al turno siguiente.
        var porRevisar = Assert.Single(envios.PorAtender);
        Assert.Equal((EstadoEnvioEleventa.PorRevisar, $"Venta {atorada.Folio}"), (porRevisar.Estado, porRevisar.Descripcion));
        Assert.StartsWith("Se hizo antes del corte de eleventa", porRevisar.Error);
        var enEleventa = await e.FilasAsync("SELECT TURNO_ID FROM VENTATICKETS WHERE CAJA_ID = 4 AND ESTA_ABIERTO = 'f'");
        Assert.Equal([segundo], enEleventa.Select(t => Convert.ToInt32(t["TURNO_ID"])));
    }

    [FactConFirebird]
    public async Task La_revision_tardia_anota_el_folio_que_otra_caja_repitio()
    {
        await using var e = await EleventaAsync();
        await e.AbrirCajaAsync(CajaEleventa, cajero: 7, HoraLocal(60), fondo: 300);
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio, await MapearAsync(servicio));
        var ticket = await CobrarAsync(c);
        await MandarAsync(c);
        Assert.Equal(42, await FolioEleventaAsync(c, ticket));

        // La principal guardó el 42 un instante después de la revisión al guardar.
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN, TOTAL) VALUES (42, 1, 1, -1, 'f', @cuando, 45.5)",
            ("cuando", HoraLocal(0)));
        Assert.Equal(HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
        var envios = await EsperarAsync(() => EnviosAsync(c), x => x.FoliosRepetidos == 1);

        Assert.Equal(1, envios.FoliosRepetidos);
        // Ya pudo salir impreso con el 42: no se cambia en eleventa, y se reimprime con el folio de la caja.
        Assert.Equal(2, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS WHERE FOLIO = 42")));
        Assert.Null(await FolioEleventaAsync(c, ticket));
        var folio = await Leer<FolioEleventaDto>(await c.Http.GetAsync("api/eleventa/folio/42"));
        Assert.Equal(["Caja 1", "Principal"], folio.Tickets.Select(t => t.Caja).Order());
        Assert.Equal(4550, folio.Tickets.Single(t => t.Caja == "Principal").TotalCentavos);
    }

    /// <summary>La lectura de eleventa, ya: espera a que termine la que se pidió.</summary>
    private static async Task LeerAsync(Caja c)
    {
        var antes = (await c.Http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa"))!.Lecturas;
        Assert.Equal(HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/leer", null)).StatusCode);
        var despues = await EsperarAsync(async () => (await c.Http.GetFromJsonAsync<EstadoEleventaDto>("api/eleventa"))!, x => x.Lecturas > antes);
        Assert.True(despues.Lecturas > antes && despues.Bien, despues.Error);
    }

    private static async Task<EleventaDePrueba> EleventaAsync(bool conComunes = true)
    {
        var e = await EleventaDePrueba.CrearAsync();
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (1, 'Principal', '10.0.0.10')");
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (4, 'Caja 2', '10.0.0.19')");
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, USUARIO) VALUES (7, 'cajera2')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (5, 'Papelería')");
        if (conComunes)
            await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (9, @nombre)", ("nombre", EnvioEleventa.DepartamentoComunes));
        await e.ProductoAsync(10, "PLU-FINA", "Pluma punto fino", depto: 5, costo: 4, precio: 9, existencia: 50);
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN) VALUES (41, 1, 1, -1, 'f', @cuando)",
            ("cuando", HoraLocal(120)));
        return e;
    }
}
