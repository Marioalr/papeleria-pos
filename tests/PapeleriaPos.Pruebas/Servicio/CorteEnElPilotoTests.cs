using System.Net;
using System.Net.Http.Json;
using PapeleriaPos.Eleventa;
using PapeleriaPos.Interfaz;
using PapeleriaPos.Nucleo;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Turnos;
using PapeleriaPos.Nucleo.Ventas;
using PapeleriaPos.Servicio;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// El corte de la caja 2 en el piloto se hace en eleventa (decisión del dueño, 2026-10-12): mientras la caja manda sus
/// ventas a eleventa (Eleventa:Escribir), el servicio no hace su corte Z y el puente no cierra el turno de eleventa (no lo
/// encola, ni manda el que ya estaba en la bandeja) ni lo abre (lo abre eleventa); solo se cancela un ticket del mismo
/// día; la caja tampoco hace el corte sin conexión, y dice lo mismo que el servicio. Sin el piloto, el corte sigue como
/// siempre. Solo para convivir con eleventa: se quita al dejarlo.
/// </summary>
public sealed class CorteEnElPilotoTests : IDisposable
{
    private readonly string _baseLocal = Path.Combine(Path.GetTempPath(), $"caja-local-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        ArchivosDePrueba.BorrarBase(_baseLocal);
    }

    /// <summary>La caja manda a un eleventa que no contesta (nadie escucha en ese puerto): lo que va allá espera en la bandeja.</summary>
    private static readonly (string, string)[] Piloto =
    [
        ("Eleventa:Escribir", "true"), ("Eleventa:Servidor", "127.0.0.1"), ("Eleventa:Puerto", "1"), ("Eleventa:Base", @"C:\eleventa\PDVDATA.FDB"),
        ("Eleventa:Clave", "masterkey"), ("Eleventa:Caja", "4"), ("Eleventa:Cajero", "7"), ("Eleventa:SegundosEntreEnvios", "3600"),
        // Sin respaldo propio de eleventa (lo prueban RespaldoDeEleventaTests y RespaldoDelPilotoTests).
        ("Eleventa:RespaldoObligatorio", "false"),
    ];

    /// <summary>Una caja de antes del 2026-10-14 sube el conteo billete por billete: un billete de 500.</summary>
    private static readonly Dictionary<long, int> Conteo = new() { [50000] = 1 };

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<string> RechazoAsync(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje;
    }

    private sealed record Caja(HttpClient Http, CajaDto Datos, SesionDto Cajero, SesionDto Encargado, TurnoDto Turno);

    private static async Task<Caja> AbrirAsync(ServicioDePrueba servicio)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        async Task<SesionDto> Entrar(string usuario, string pin) => await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada(usuario, pin)));
        var cajero = await Entrar("cajero", "1111");
        var turno = await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)));
        return new Caja(http, caja, cajero, await Entrar("encargado", "2222"), turno);
    }

    /// <summary>Un artículo común de $25 en efectivo: en el piloto, también va a la bandeja hacia eleventa.</summary>
    private static async Task<TicketDto> EnEfectivoAsync(Caja c) =>
        await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId,
            [new LineaSolicitada(null, null, 1000, "Recuerdo", 2500)], [new CobroSolicitado(FormaPago.Efectivo, 5000)])));

    /// <summary>El encargado, que ya entró, cuenta $500.</summary>
    private static Task<HttpResponseMessage> CorteZAsync(Caja c) =>
        c.Http.PostAsJsonAsync($"api/turnos/{c.Turno.Id}/corte-z", new SolicitudCorteZ(c.Encargado.UsuarioId, 50000));

    /// <summary>El turno abierto de la caja según el servicio; vacío si ya se cerró.</summary>
    private static async Task<Guid?> TurnoAbiertoAsync(Caja c)
    {
        var r = await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/turno");
        return r.StatusCode == HttpStatusCode.NoContent ? null : (await Leer<TurnoDto>(r)).Id;
    }

    private static async Task<EstadoEnviosEleventaDto> EsperarAsync(Caja c, Func<EstadoEnviosEleventaDto, bool> listo)
    {
        for (var intento = 0; ; intento++)
        {
            var estado = (await c.Http.GetFromJsonAsync<EstadoEnviosEleventaDto>("api/eleventa/envios"))!;
            if (listo(estado) || intento == 100)
                return estado;
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task En_el_piloto_el_servicio_no_hace_el_corte_z_ni_encola_el_cierre_de_eleventa()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Piloto);
        var c = await AbrirAsync(servicio);
        var venta = await EnEfectivoAsync(c);

        var corte = await RechazoAsync(await CorteZAsync(c));
        // El parcial sí: es informativo, no cierra nada ni va a eleventa (la caja ya no lo ofrece en el piloto).
        var parcial = await Leer<CorteDto>(await c.Http.PostAsync($"api/turnos/{c.Turno.Id}/corte-x?usuarioId={c.Cajero.UsuarioId}", null));

        Assert.Equal(CorteEnElPiloto.CorteEnEleventa, corte);
        Assert.Equal(TipoCorte.X, parcial.Tipo);
        Assert.Equal(c.Turno.Id, await TurnoAbiertoAsync(c));
        await using var db = servicio.Base();
        Assert.False(db.Cortes.Any(x => x.TurnoId == c.Turno.Id && x.Tipo == TipoCorte.Z));
        // En la bandeja hacia eleventa solo va la venta: el turno de eleventa sigue abierto para su corte.
        Assert.Equal([(TipoEnvioEleventa.Venta, venta.VentaId)], db.EnviosAEleventa.Select(e => new { e.Tipo, e.VentaId }).AsEnumerable()
            .Select(e => (e.Tipo, e.VentaId)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sin_el_piloto_el_corte_z_cierra_el_turno_como_siempre(bool soloLeeEleventa)
    {
        // Solo leer el catálogo de eleventa (Eleventa:Activo) no es el piloto: no manda nada allá.
        await using var servicio = soloLeeEleventa
            ? await ServicioDePrueba.CrearAsync(("Eleventa:Activo", "true"))
            : await ServicioDePrueba.CrearAsync();
        var c = await AbrirAsync(servicio);
        await EnEfectivoAsync(c);

        var corte = await Leer<CorteDto>(await CorteZAsync(c));

        Assert.Equal((TipoCorte.Z, 50000L), (corte.Tipo, corte.ContadoCentavos));
        Assert.Null(await TurnoAbiertoAsync(c));
        Assert.False((await c.Http.GetFromJsonAsync<EstadoServicioDto>("api/estado"))!.PilotoEleventa);
    }

    [Fact]
    public async Task Un_corte_z_sin_conexion_que_llega_en_el_piloto_se_guarda_y_su_cierre_queda_en_la_bandeja()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Piloto);
        var c = await AbrirAsync(servicio);
        await EnEfectivoAsync(c);

        // Lo hizo sin conexión una caja de antes (o de antes del piloto): ya pasó. Si no se guardara, lo que esa caja hizo
        // después (otro turno) no entraría.
        var subida = await c.Http.PostAsJsonAsync($"api/bandeja/turnos/{c.Turno.Id}/corte-z",
            new CorteContingenciaDto(Ids.Nuevo(), c.Encargado.UsuarioId, Conteo, DateTime.UtcNow));

        Assert.True(subida.IsSuccessStatusCode, await subida.Content.ReadAsStringAsync());
        Assert.Null(await TurnoAbiertoAsync(c));
        // Encola el cierre como siempre, después de su venta; el puente no lo manda: queda por revisar (la prueba de abajo).
        await using var db = servicio.Base();
        Assert.Equal([TipoEnvioEleventa.Venta, TipoEnvioEleventa.CierreDeTurno], db.EnviosAEleventa.OrderBy(e => e.CreadoUtc).Select(e => e.Tipo).ToList());
    }

    [Fact]
    public async Task Un_cierre_que_ya_estaba_en_la_bandeja_no_se_manda_en_el_piloto()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Piloto);
        var c = await AbrirAsync(servicio);
        // De antes: un corte Z dejó en la bandeja el cierre del turno de eleventa (eleventa no contestaba).
        await using (var db = servicio.Base())
        {
            db.EnviosAEleventa.Add(new EnvioAEleventa { Tipo = TipoEnvioEleventa.CierreDeTurno, TurnoId = c.Turno.Id, CreadoUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
        var envios = await EsperarAsync(c, x => x.PorRevisar == 1 && x.PorAtender.All(e => e.Estado == EstadoEnvioEleventa.PorRevisar));

        // Ni se intentó: queda por revisar con el porqué, y lo que siga en la bandeja pasa al turno de eleventa, que sigue abierto.
        var cierre = envios.PorAtender.Single();
        Assert.Equal((0, 1), (envios.Pendientes, envios.PorRevisar));
        Assert.Equal((TipoEnvioEleventa.CierreDeTurno, EstadoEnvioEleventa.PorRevisar, 0), (cierre.Tipo, cierre.Estado, cierre.Intentos));
        Assert.Equal(CorteEnElPiloto.NoSeCierraEleventa, cierre.Error);
    }

    [Fact]
    public async Task La_caja_no_hace_el_corte_en_el_piloto_tampoco_sin_conexion()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Piloto);
        var c = await AbrirAsync(servicio);
        var interruptor = new Interruptor(servicio.Fabrica.Server.CreateHandler());
        var http = new HttpClient(interruptor);
        var conexion = new ConexionCaja { Servicio = new Uri("http://localhost/"), CajaId = c.Datos.Id };
        var contingencia = new Contingencia(new AlmacenLocal(_baseLocal), http, conexion);
        var cliente = new ClienteCaja(http, conexion, contingencia);
        await contingencia.LatidoAsync();
        // La caja conoce su turno (sin conexión, trabaja en él).
        Assert.Equal(c.Turno.Id, (await cliente.TurnoAsync(c.Datos.Id))!.Id);
        var solicitud = new SolicitudCorteZ(c.Encargado.UsuarioId, 50000);

        var enLinea = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.CorteZAsync(c.Turno.Id, solicitud));
        // Se apaga la caja principal: la caja sigue sabiendo del piloto (también si arranca otra vez sin ella) y no cierra
        // el turno con su copia.
        interruptor.Caido = true;
        await contingencia.LatidoAsync();
        var otraVez = new Contingencia(new AlmacenLocal(_baseLocal), http, conexion);
        var sinConexion = await Assert.ThrowsAsync<ErrorCaja>(() => cliente.CorteZAsync(c.Turno.Id, solicitud));
        var reiniciada = Assert.Throws<InvalidOperationException>(() => otraVez.CorteZ(c.Turno.Id, solicitud));

        Assert.Equal(CorteEnElPiloto.CorteEnEleventa, enLinea.Message);
        Assert.Equal((PilotoConEleventa.CorteEnEleventa, PilotoConEleventa.CorteEnEleventa), (sinConexion.Message, reiniciada.Message));
        // El turno sigue abierto en la caja y en el servicio, y nada espera en la bandeja para cerrarlo.
        Assert.Equal(c.Turno.Id, (await cliente.TurnoAsync(c.Datos.Id))!.Id);
        Assert.Equal((0, 0), (contingencia.Pendientes, contingencia.ConError));
        Assert.Equal(c.Turno.Id, await TurnoAbiertoAsync(c));
    }

    [Fact]
    public async Task Sin_el_piloto_el_corte_z_encola_el_cierre_de_lo_que_fue_a_eleventa()
    {
        // Lo que queda para cuando la caja tome el corte de eleventa: fuera del piloto, si algo del turno fue a eleventa
        // (aquí, de cuando la caja mandaba), el corte Z sigue encolando el cierre de su turno allá.
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var c = await AbrirAsync(servicio);
        var venta = await EnEfectivoAsync(c);
        await using (var db = servicio.Base())
        {
            db.EnviosAEleventa.Add(new EnvioAEleventa { Tipo = TipoEnvioEleventa.Venta, VentaId = venta.VentaId, CreadoUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await Leer<CorteDto>(await CorteZAsync(c));

        await using var revision = servicio.Base();
        Assert.Equal([TipoEnvioEleventa.Venta, TipoEnvioEleventa.CierreDeTurno],
            revision.EnviosAEleventa.OrderBy(e => e.CreadoUtc).Select(e => e.Tipo).ToList());
    }

    [Fact]
    public async Task En_el_piloto_solo_se_cancela_un_ticket_del_mismo_dia()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Piloto);
        var c = await AbrirAsync(servicio);
        var hoy = await EnEfectivoAsync(c);
        var ayer = await EnEfectivoAsync(c);
        // El turno de la caja nueva no se cierra en el piloto: un ticket de ayer sigue en él, pero allá ya se cortó.
        await using (var db = servicio.Base())
        {
            var pagada = db.Entry((await db.Ventas.FindAsync(ayer.VentaId))!).Property(v => v.PagadaUtc);
            pagada.CurrentValue = pagada.CurrentValue!.Value.AddDays(-1);
            await db.SaveChangesAsync();
        }
        Task<VentaParaDevolverDto?> Buscar(TicketDto t) =>
            c.Http.GetFromJsonAsync<VentaParaDevolverDto>($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{t.Folio}");
        Task<HttpResponseMessage> Cancelar(TicketDto t) =>
            c.Http.PostAsJsonAsync($"api/ventas/{t.VentaId}/cancelacion", new SolicitudCancelacion(c.Encargado.UsuarioId, c.Datos.Id, "Se arrepintió"));

        Assert.Equal((true, false), ((await Buscar(hoy))!.PuedeCancelar, (await Buscar(ayer))!.PuedeCancelar));
        Assert.Equal(CorteEnElPiloto.SoloSeCancelaElMismoDia, await RechazoAsync(await Cancelar(ayer)));
        await Leer<ComprobanteDevolucionDto>(await Cancelar(hoy));
    }

    [Fact]
    public void En_el_piloto_el_puente_no_abre_el_turno_de_eleventa()
    {
        // Lo abre eleventa con su fondo; el de la caja nueva no se cierra cada día y pondría allá los del primer día.
        OpcionesEleventa Opciones(bool escribir) => new()
        {
            Escribir = escribir, Servidor = "127.0.0.1", Base = @"C:\eleventa\PDVDATA.FDB", Clave = "masterkey", Caja = 4, Cajero = 7,
        };

        Assert.False(Opciones(escribir: true).ParaEscribir()!.Value.Caja.AbreTurno);
        Assert.Null(Opciones(escribir: false).ParaEscribir());
        // Fuera del piloto (las herramientas que escriben en una copia), el puente lo abre como eleventa.
        Assert.True(new CajaEnEleventa(4, 7).AbreTurno);
    }

    [Fact]
    public async Task En_el_piloto_el_corte_del_dia_se_ve_pero_no_se_cierra_ni_toca_eleventa()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Piloto);
        var c = await AbrirAsync(servicio);
        await EnEfectivoAsync(c);
        var ayer = await EnEfectivoAsync(c);
        // El turno de la caja nueva no se cierra en el piloto: viene de ayer, con un ticket de ayer.
        await using (var antes = servicio.Base())
        {
            var pagada = antes.Entry((await antes.Ventas.FindAsync(ayer.VentaId))!).Property(v => v.PagadaUtc);
            pagada.CurrentValue = pagada.CurrentValue!.Value.AddDays(-1);
            var abierto = antes.Entry((await antes.Turnos.FindAsync(c.Turno.Id))!).Property(t => t.AbiertoUtc);
            abierto.CurrentValue = abierto.CurrentValue.AddDays(-1);
            await antes.SaveChangesAsync();
        }
        var dueno = await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));

        var dia = await Leer<CorteDelDiaDto>(await c.Http.GetAsync($"api/cajas/{c.Datos.Id}/corte-del-dia?usuarioId={dueno.UsuarioId}"));
        var cierre = await RechazoAsync(await c.Http.PostAsJsonAsync($"api/cajas/{c.Datos.Id}/corte-del-dia/cierre", new SolicitudCierreDia(dueno.UsuarioId)));

        Assert.True(dia.Piloto);
        var turno = Assert.Single(dia.Turnos);
        // «Vendido hoy» es lo de hoy, no lo de todo el piloto.
        Assert.Equal((1, 2500L), (turno.Tickets, turno.VendidoCentavos));
        Assert.Equal((1, 2500L, 2500L), (dia.Tickets, dia.VendidoCentavos, dia.EfectivoCentavos));
        // Su turno no se cierra aquí y suma días: lo esperado no se enseña.
        Assert.Null(turno.EsperadoCentavos);
        Assert.Equal(CorteEnElPiloto.CorteDelDiaEnEleventa, cierre);
        // Ver el día o intentar cerrarlo no manda nada a eleventa: en la bandeja solo están las ventas.
        await using var db = servicio.Base();
        Assert.Equal([TipoEnvioEleventa.Venta, TipoEnvioEleventa.Venta], db.EnviosAEleventa.Select(e => e.Tipo).ToList());
        Assert.False(db.Bitacora.Any(b => b.Accion == PapeleriaPos.Nucleo.Control.AccionControlada.CorteDelDia));
    }

    [Fact]
    public void La_caja_dice_lo_mismo_que_el_servicio()
    {
        // La interfaz no conoce el puente: copia sus textos. Con o sin conexión, la cajera lee lo mismo.
        Assert.Equal(CorteEnElPiloto.CorteEnEleventa, PilotoConEleventa.CorteEnEleventa);
        Assert.Equal(CorteEnElPiloto.CorteDelDiaEnEleventa, PilotoConEleventa.CorteDelDiaEnEleventa);
        Assert.Equal(CorteEnElPiloto.SoloSeCancelaElMismoDia, PilotoConEleventa.SoloSeCancelaElMismoDia);
    }
}
