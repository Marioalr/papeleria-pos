using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Pruebas.ControlPrint;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using static PapeleriaPos.Impresiones.Pruebas.ControlPrint.PorCobrarDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Lo que encontró la revisión del PR #146 (2026-10-29) al cobrar las tarjetas de Control Print, por el servicio de verdad contra el
/// Control Print falso (que hace fallar la prueba si el POS le escribe): lo cobrado en la caja 2 que después se cobra en la caja
/// principal se nota y se queda guardado; si Control Print no contesta al cobrar, la tarjeta no queda para cobrarse dos veces; lo que
/// falta por cobrar no se esconde detrás de lo que la caja 2 ya cobró; lo que tiene hojas por revisar no se cobra aquí; las copias del
/// panel van en su propia línea; y dos cobros a la vez de la misma tarjeta dejan una sola venta.
/// </summary>
public sealed class ControlPrintAlCobrarTests : IAsyncLifetime
{
    private ServidorControlPrintFalso _controlPrint = null!;
    private ServicioDePrueba _servicio = null!;
    private readonly RegistroCapturado _registro = new();

    /// <summary>La hora del servicio: solo avanza cuando la prueba lo dice (para «lo último que se leyó es de hace más de 10 minutos»).</summary>
    private readonly RelojDePrueba _reloj = new(DateTime.UtcNow);

    public async Task InitializeAsync()
    {
        _controlPrint = await ServidorControlPrintFalso.IniciarAsync(Codigo);
        _servicio = await ServicioDePrueba.CrearAsync(s =>
        {
            s.AddSingleton<ILoggerProvider>(_registro);
            s.AddSingleton<TimeProvider>(_reloj);
        }, ControlPrintEnLaCajaTests.Ajustes(_controlPrint));
    }

    public async Task DisposeAsync()
    {
        await _servicio.DisposeAsync();
        // Al terminar, el falso hace fallar la prueba si alguien le escribió.
        await _controlPrint.DisposeAsync();
    }

    private sealed record Caja(HttpClient Http, CajaDto Datos, SesionDto Cajero, SesionDto Dueno, TurnoDto Turno);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<Caja> AbrirAsync()
    {
        var http = _servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var turno = await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)));
        return new Caja(http, caja, cajero, dueno, turno);
    }

    private static async Task<ColaControlPrintDto> ColaAsync(Caja c) =>
        await Leer<ColaControlPrintDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/impresiones/control-print?usuarioId={c.Cajero.UsuarioId}"));

    private static async Task<ColaControlPrintDto> HastaQueAsync(Caja c, Func<ColaControlPrintDto, bool> listo, string que)
    {
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var cola = await ColaAsync(c);
            if (listo(cola))
                return cola;
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"No pasó: {que} (Control Print: {cola.Aviso ?? "contesta"}, {cola.Trabajos.Count} tarjetas).");
            await Task.Delay(50);
        }
    }

    private static SolicitudCobro Cobro(Caja c, params ImpresionDeControlPrintSolicitada[] controlPrint) =>
        new(c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId, [], [new CobroSolicitado(FormaPago.Efectivo, 100000)], VentaId: Guid.NewGuid(),
            ImpresionesControlPrint: controlPrint);

    private void ApagarControlPrint()
    {
        _controlPrint.Antes = (ctx, _) =>
        {
            ctx.Abort();
            return Task.FromResult(true);
        };
        _controlPrint.Avisar(0);
    }

    private void PrenderControlPrint()
    {
        _controlPrint.Antes = null;
        _controlPrint.Avisar(0);
    }

    private TrabajoControlPrintCobrado Cobrado(long id)
    {
        using var db = _servicio.Base();
        return db.Set<TrabajoControlPrintCobrado>().AsNoTracking().Single(x => x.TrabajoId == id && x.LiberadoUtc == null);
    }

    /// <summary>
    /// ALTA. El POS nunca le escribe a Control Print, así que lo cobrado en la caja 2 sigue «por cobrar» allá, y la caja principal lo
    /// puede cobrar otra vez (un «Cobrar 3 · $X» de Control Print que junta en su lote lo de otro cliente). No se puede evitar sin
    /// escribirle; se nota: cuando deja de estar por cobrar allá, se lee y, si allá también se cobró, se dice qué trabajo, en qué
    /// venta de aquí y quién lo cobró allá (F2, el chip de arriba, /api/controlprint y el registro del servicio), y se queda guardado.
    /// </summary>
    [Fact]
    public async Task Lo_cobrado_en_la_caja_2_que_despues_se_cobra_en_Control_Print_se_nota()
    {
        _controlPrint.CambiarTrabajos(t => t.Add(Trabajo(1234, Hace(4), precio: 15, documento: "Tesis.pdf")));
        var c = await AbrirAsync();
        await HastaQueAsync(c, x => x.Trabajos.Count == 1, "la tarjeta #1234");

        var ticket = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, new ImpresionDeControlPrintSolicitada(1234, 1500))));
        Assert.Empty((await ColaAsync(c)).Trabajos);

        // En la caja principal, Ana cobra en Control Print el lote donde quedó el #1234 (allá seguía «por cobrar»).
        _controlPrint.CambiarTrabajos(t =>
        {
            var cobrado = t.Single(x => x.Id == 1234);
            (cobrado.Payment, cobrado.PaymentBy, cobrado.PaymentUtc) = (PaymentState.Paid, "Ana", DateTime.UtcNow);
        });

        var cola = await HastaQueAsync(c, x => x.CobradasDosVeces is { Count: > 0 }, "que diga que se cobró dos veces");
        var aviso = Assert.Single(cola.CobradasDosVeces!);
        Assert.StartsWith($"#1234 (Tesis.pdf) se cobró dos veces: aquí, en la venta {ticket.Folio} ($15.00", aviso);
        Assert.Contains("y en Control Print, en la caja principal (Ana, ", aviso);
        Assert.Contains("Revisa con el cliente y devuélvele uno.", aviso);
        // El chip de arriba, /api/controlprint y el registro del servicio lo dicen igual.
        var resumen = await Leer<ResumenPorCobrarDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/impresiones/por-cobrar/cuantas"));
        Assert.Equal([aviso], resumen.CobradasDosVeces);
        Assert.Contains(aviso, (await Leer<EstadoControlPrintDto>(await c.Http.GetAsync("api/controlprint"))).Avisos!);
        Assert.Contains(_registro.Lineas, l => l.Nivel == LogLevel.Warning && l.Texto == aviso);
        // Se queda guardado (sigue después de reiniciar) y no se vuelve a revisar.
        var guardado = Cobrado(1234);
        Assert.Equal("Ana", guardado.CobradoEnControlPrintPor);
        Assert.NotNull(guardado.CobradoEnControlPrintUtc);
        Assert.NotNull(guardado.RevisadoUtc);
        Assert.Empty(_controlPrint.Escrituras);
    }

    /// <summary>
    /// MEDIA (antes, «Cobrarla_a_mano_con_IMP_como_dice_el_409_no_la_deja_para_cobrarla_otra_vez»). Si Control Print no contesta al
    /// cobrar, ya no se dice «cóbralo a mano con IMP» (lo cobrado a mano no queda ligado al trabajo y su tarjeta volvía a salir):
    /// con lo último que se leyó de ella, de hace 10 minutos o menos, se cobra y queda «sin revisar»; si es de antes, no se cobra
    /// («cóbrala cuando conteste»). En los dos casos, la tarjeta nunca se cobra dos veces.
    /// </summary>
    [Fact]
    public async Task Si_Control_Print_no_contesta_al_cobrar_la_tarjeta_no_se_cobra_dos_veces()
    {
        _controlPrint.CambiarTrabajos(t => t.AddRange([Trabajo(1234, Hace(4), precio: 15), Trabajo(1235, Hace(3), precio: 2)]));
        var c = await AbrirAsync();
        await HastaQueAsync(c, x => x.Trabajos.Count == 2, "las tarjetas");

        // La caja principal no contesta justo cuando el cliente paga en la caja 2: se cobra con lo último que se leyó (hace un momento).
        ApagarControlPrint();
        await HastaQueAsync(c, x => x.Contesta == false, "que diga que no contesta");
        var ticket = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, new ImpresionDeControlPrintSolicitada(1234, 1500))));
        Assert.Equal(1500, ticket.TotalCentavos);
        Assert.True(Cobrado(1234).SinRevisar);
        // Otra vez la misma, no.
        var otraVez = await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, new ImpresionDeControlPrintSolicitada(1234, 1500)));
        Assert.Equal(HttpStatusCode.Conflict, otraVez.StatusCode);
        Assert.Contains("#1234 ya se cobró en esta caja", (await otraVez.Content.ReadFromJsonAsync<ControlPrintAlCobrarDto>())!.Mensaje);

        // Pasan 11 minutos sin que conteste: lo último que se leyó del #1235 ya es viejo y no se cobra; nunca «a mano».
        _reloj.Avanzar(TimeSpan.FromMinutes(11));
        var vieja = await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, new ImpresionDeControlPrintSolicitada(1235, 200)));
        Assert.Equal(HttpStatusCode.Conflict, vieja.StatusCode);
        var cambio = (await vieja.Content.ReadFromJsonAsync<ControlPrintAlCobrarDto>())!;
        Assert.Equal([1235L], cambio.Quitar);
        Assert.Contains("Control Print no contesta: cobra #1235 cuando conteste", cambio.Mensaje);
        Assert.DoesNotContain("IMP", cambio.Mensaje);
        Assert.DoesNotContain("a mano", cambio.Mensaje);

        // Vuelve a contestar: el #1234 queda revisado (allá sigue por cobrar) y no sale; el #1235 sale y se cobra una vez.
        PrenderControlPrint();
        var cola = await HastaQueAsync(c, x => x.Contesta == true && x.Trabajos.Count == 1, "que vuelva y ofrezca el #1235");
        Assert.Equal([1235L], cola.Trabajos.Select(t => t.Id));
        await HastaAsync(() => !Cobrado(1234).SinRevisar, "que revise el #1234");
        Assert.Equal(200, (await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, new ImpresionDeControlPrintSolicitada(1235, 200))))).TotalCentavos);
        foreach (var id in new[] { 1234L, 1235L })
        {
            var tercera = await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, new ImpresionDeControlPrintSolicitada(id, id == 1234 ? 1500 : 200)));
            Assert.Equal(HttpStatusCode.Conflict, tercera.StatusCode);
        }
        Assert.Empty((await ColaAsync(c)).Trabajos);
        Assert.Empty(_controlPrint.Escrituras);
    }

    /// <summary>
    /// BAJA. Lo cobrado en la caja 2 sigue «por cobrar» en Control Print: en un día de mucho trabajo, los 200 más nuevos podían ser todos
    /// de la caja 2 y lo que de verdad falta (más viejo) ya no salía en F2. La cola lee todos los de los días que se ven y el servicio
    /// quita primero lo cobrado aquí y después ofrece hasta 200.
    /// </summary>
    [Fact]
    public async Task Lo_que_falta_por_cobrar_no_se_esconde_detras_de_lo_que_la_caja_2_ya_cobro()
    {
        _controlPrint.CambiarTrabajos(t =>
        {
            // Cinco de hace 40 minutos que nadie ha cobrado, y 200 más nuevos que se cobraron en la caja 2 (allá siguen «por cobrar»).
            for (var id = 1; id <= 5; id++)
                t.Add(Trabajo(id, Hace(40 + id), precio: 4));
            for (var id = 101; id <= 300; id++)
                t.Add(Trabajo(id, Hace(30 - (id - 100) * 0.1), precio: 4));
        });
        var c = await AbrirAsync();
        await HastaQueAsync(c, x => x.Trabajos.Count == 200, "las 200 más nuevas");
        // La caja 2 cobra el #300 por la API y los otros 199 se apuntan igual que lo haría cada cobro (la misma venta, para no tardar).
        var ticket = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, new ImpresionDeControlPrintSolicitada(300, 400))));
        await using (var db = _servicio.Base())
        {
            var hora = DateTime.UtcNow;
            foreach (var t in _controlPrint.Trabajos.Where(t => t.Id is >= 101 and < 300))
                db.Set<TrabajoControlPrintCobrado>().Add(new TrabajoControlPrintCobrado
                {
                    SucursalId = c.Datos.SucursalId, TrabajoId = t.Id, DetectadoUtc = t.DetectedUtc, VentaId = ticket.VentaId!.Value, Documento = t.Document,
                    Pc = t.SourcePc, PrecioCentavos = 400, CostoCentavos = 112, CobradoUtc = hora,
                });
            await db.SaveChangesAsync();
        }

        var cola = await ColaAsync(c);

        Assert.Equal([1L, 2, 3, 4, 5], cola.Trabajos.Select(t => t.Id).Order());
        Assert.Equal(0, cola.Mas);
        Assert.Empty(_controlPrint.Escrituras);
    }

    /// <summary>
    /// BAJA. Una de Control Print con «Revisar hojas» (la impresora contó menos) aquí solo se cobraría completa: no se cobra hasta que
    /// en la caja principal la resuelvan («cobrar lo que salió» o «no vendida»); después sí, con el precio de lo que salió.
    /// </summary>
    [Fact]
    public async Task Una_tarjeta_con_hojas_por_revisar_no_se_cobra_hasta_que_se_resuelve_en_la_caja_principal()
    {
        var conHojas = Trabajo(1234, Hace(4), precio: 15);
        conHojas.CountedSides = 1;
        _controlPrint.CambiarTrabajos(t => t.Add(conHojas));
        var c = await AbrirAsync();
        var tarjeta = (await HastaQueAsync(c, x => x.Trabajos.Count == 1, "la tarjeta")).Trabajos.Single();
        Assert.NotNull(tarjeta.RevisarHojas);

        var respuesta = await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, new ImpresionDeControlPrintSolicitada(1234, 1500)));
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var cambio = (await respuesta.Content.ReadFromJsonAsync<ControlPrintAlCobrarDto>())!;
        Assert.Equal([1234L], cambio.Quitar);
        Assert.Contains("#1234 tiene hojas por revisar", cambio.Mensaje);

        // En la caja principal cobran solo lo que salió: ya se puede cobrar aquí, a ese precio.
        _controlPrint.CambiarTrabajos(t => (t.Single(x => x.Id == 1234).PriceOverride, t.Single(x => x.Id == 1234).PriceKind) = (8, PriceKind.Partial));
        await HastaQueAsync(c, x => x.Trabajos is [{ RevisarHojas: null, PrecioCentavos: 800 }], "que ya no tenga hojas por revisar");
        Assert.Equal(800, (await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, new ImpresionDeControlPrintSolicitada(1234, 800))))).TotalCentavos);
        Assert.Empty(_controlPrint.Escrituras);
    }

    /// <summary>
    /// Las copias del panel de la impresora van en su propia línea, aparte de las impresiones (2026-10-29: en el piloto, COP y no IMP,
    /// como las separa la tienda). Devolver completa la de copias solo las vuelve a ofrecer a ellas.
    /// </summary>
    [Fact]
    public async Task Las_copias_del_panel_van_en_su_propia_linea_y_se_devuelven_aparte()
    {
        var copias = Trabajo(1240, Hace(3), precio: 6, documento: "Copias en la impresora");
        copias.Kind = JobKind.PanelCopy;
        _controlPrint.CambiarTrabajos(t => t.AddRange([Trabajo(1234, Hace(4), precio: 15), copias]));
        var c = await AbrirAsync();
        await HastaQueAsync(c, x => x.Trabajos.Count == 2, "las dos tarjetas");

        var ticket = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas",
            Cobro(c, new ImpresionDeControlPrintSolicitada(1234, 1500), new ImpresionDeControlPrintSolicitada(1240, 600))));

        await using (var db = _servicio.Base())
        {
            var lineas = await db.Ventas.Where(v => v.Id == ticket.VentaId).SelectMany(v => v.Lineas).OrderBy(l => l.EsCopias).ToListAsync();
            Assert.Equal([("Impresión #1234", false, 1500L), ("Copias #1240", true, 600L)],
                lineas.Select(l => (l.Descripcion, l.EsCopias, l.ImporteCentavos)));
            Assert.All(lineas, l => Assert.True(l.EsImpresiones));
            Assert.True((await db.Set<TrabajoControlPrintCobrado>().SingleAsync(x => x.TrabajoId == 1240)).EsCopia);
        }

        // Se devuelve completa la línea de copias: solo la tarjeta de copias vuelve a salir.
        await using (var db = _servicio.Base())
        {
            var deCopias = await db.VentasLineas.SingleAsync(l => l.VentaId == ticket.VentaId && l.EsCopias);
            await Leer<object>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
                new SolicitudDevolucion(c.Dueno.UsuarioId, c.Datos.Id, "No eran", [new LineaDevolucionSolicitada(deCopias.Id, deCopias.Cantidad.Milesimas)])));
        }
        var cola = await HastaQueAsync(c, x => x.Trabajos.Count == 1, "la de copias otra vez");
        Assert.Equal([1240L], cola.Trabajos.Select(t => t.Id));
        Assert.Empty(_controlPrint.Escrituras);
    }

    /// <summary>
    /// Comprobación: dos tickets que cobran a la vez la misma tarjeta, con Control Print tardando un poco en contestar (así los dos
    /// pasan la revisión antes de guardar): el índice único deja una sola venta y el otro recibe un error claro.
    /// </summary>
    [Fact]
    public async Task Dos_cobros_a_la_vez_de_la_misma_tarjeta_dejan_una_venta()
    {
        _controlPrint.CambiarTrabajos(t => t.Add(Trabajo(1234, Hace(4), precio: 15)));
        var c = await AbrirAsync();
        await HastaQueAsync(c, x => x.Trabajos.Count == 1, "la tarjeta #1234");
        _controlPrint.Antes = async (_, p) =>
        {
            if (p.Ruta == "/api/jobs/search")
                await Task.Delay(300);
            return false;
        };

        var respuestas = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            c.Http.PostAsJsonAsync("api/ventas", Cobro(c, new ImpresionDeControlPrintSolicitada(1234, 1500)))));

        var estados = respuestas.Select(r => r.StatusCode).OrderBy(s => (int)s).ToList();
        var textos = await Task.WhenAll(respuestas.Select(r => r.Content.ReadAsStringAsync()));
        Assert.True(estados[0] == HttpStatusCode.OK && estados[1] is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
            $"{string.Join(" / ", estados)}: {string.Join(" / ", textos)}");
        await using var db = _servicio.Base();
        Assert.Equal(1, await db.Set<TrabajoControlPrintCobrado>().CountAsync(x => x.TrabajoId == 1234 && x.LiberadoUtc == null));
        Assert.Equal(1, await db.Ventas.CountAsync(v => v.TurnoId == c.Turno.Id));
        Assert.Empty(_controlPrint.Escrituras);
    }
}

/// <summary>Lo que el servicio manda a su registro, para ver qué se dijo.</summary>
internal sealed class RegistroCapturado : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Nivel, string Categoria, string Texto)> Lineas { get; } = new();

    public ILogger CreateLogger(string categoria) => new Capturador(this, categoria);

    public void Dispose()
    {
    }

    private sealed class Capturador(RegistroCapturado dueno, string categoria) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel nivel) => true;

        public void Log<TState>(LogLevel nivel, EventId id, TState estado, Exception? ex, Func<TState, Exception?, string> formato) =>
            dueno.Lineas.Enqueue((nivel, categoria, formato(estado, ex) + (ex is null ? "" : $" {ex.GetType().Name}: {ex.Message}")));
    }
}
