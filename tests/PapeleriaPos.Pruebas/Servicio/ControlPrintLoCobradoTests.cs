using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PapeleriaPos.Datos;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.Pruebas.ControlPrint;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Telegram;
using PapeleriaPos.Nucleo.Ventas;
using static PapeleriaPos.Impresiones.Pruebas.ControlPrint.PorCobrarDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Lo cobrado aquí y en Control Print, y el cobro cuando Control Print contesta a medias o no contesta, según la segunda revisión del
/// PR #146 (2026-10-29), con el servicio de verdad contra el Control Print falso (que hace fallar la prueba si el POS le escribe): lo que
/// la caja principal cobra con el «Cobrar» de una sola tarjeta (Control Print no lo avisa) o días después se nota; una revisión que
/// contesta a medias no se tira; lo cancelado allá no se busca en cada lectura; devolver la línea de una venta de antes suelta todo; y
/// el aviso al dueño no se apaga con los de la impresora. Las «Comprobacion_…» cuidan lo que la revisión vio bien.
/// </summary>
public sealed class ControlPrintLoCobradoTests : IAsyncLifetime
{
    private ServidorControlPrintFalso _controlPrint = null!;
    private ServicioDePrueba _servicio = null!;
    private readonly RegistroCapturado _registro = new();
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

    private static async Task<EstadoControlPrintDto> EstadoAsync(Caja c) => await Leer<EstadoControlPrintDto>(await c.Http.GetAsync("api/controlprint"));

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

    /// <summary>Espera (hasta 30 s) a que la cola se haya leído al menos esas veces.</summary>
    private static async Task HastaLecturasAsync(Caja c, int lecturas)
    {
        var limite = DateTime.UtcNow.AddSeconds(30);
        while ((await EstadoAsync(c)).Lecturas < lecturas)
        {
            if (DateTime.UtcNow > limite)
                throw new Xunit.Sdk.XunitException($"No llegó a {lecturas} lecturas de la cola.");
            await Task.Delay(50);
        }
    }

    private static SolicitudCobro Cobro(Caja c, params ImpresionDeControlPrintSolicitada[] controlPrint) =>
        new(c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId, [], [new CobroSolicitado(FormaPago.Efectivo, 100000)], VentaId: Guid.NewGuid(),
            ImpresionesControlPrint: controlPrint);

    /// <summary>
    /// Un ticket con dos tarjetas de horas lejanas (más de 10 minutos) se revisa con dos búsquedas a la vez. La del #1 contesta enseguida
    /// que en la caja principal Ana ya lo cobró (la cola aún no lo leía); la del #2 tarda más que el medio segundo. Lo que contestó cuenta:
    /// el #1 no se cobra (ni «sin revisar»), aunque el #2 no haya contestado. Antes se tiraba todo y las dos se cobraban «sin revisar».
    /// </summary>
    [Fact]
    public async Task Una_revision_que_contesta_a_medias_no_cobra_lo_que_Control_Print_ya_dijo_que_se_cobro()
    {
        var viejo = Hace(40);
        var nuevo = Hace(5);
        _controlPrint.CambiarTrabajos(t => t.AddRange([Trabajo(1, viejo, precio: 4), Trabajo(2, nuevo, precio: 4)]));
        var c = await AbrirAsync();
        await HastaQueAsync(c, x => x.Trabajos.Count == 2, "las dos tarjetas");

        // En la caja principal Ana cobra el #1 justo ahora (la cola de la caja 2 todavía no lo vuelve a leer).
        var uno = _controlPrint.Trabajos.Single(x => x.Id == 1);
        (uno.Payment, uno.PaymentBy, uno.PaymentUtc) = (PaymentState.Paid, "Ana", DateTime.UtcNow);
        // Control Print contesta enseguida la búsqueda de la hora del #1 y tarda 1.5 s la de la hora del #2.
        var pedidas = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _controlPrint.Antes = async (ctx, p) =>
        {
            if (p.Ruta != "/api/jobs/search" || await ServidorControlPrintFalso.BusquedaDeAsync(ctx) is not { FromUtc: { } desde, ToUtc: { } hasta })
                return false;
            var delDos = nuevo >= desde.ToUniversalTime() && nuevo < hasta.ToUniversalTime();
            var delUno = viejo >= desde.ToUniversalTime() && viejo < hasta.ToUniversalTime();
            pedidas.Enqueue($"{(delUno ? "#1" : "")}{(delDos ? "#2" : "")}{(delDos ? " (tarda 1.5 s)" : " (enseguida)")}");
            if (delDos)
                await Task.Delay(TimeSpan.FromSeconds(1.5));
            return false;
        };

        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var respuesta = await c.Http.PostAsJsonAsync("api/ventas",
            Cobro(c, new ImpresionDeControlPrintSolicitada(1, 400), new ImpresionDeControlPrintSolicitada(2, 400)));
        var tardo = reloj.Elapsed;
        var texto = await respuesta.Content.ReadAsStringAsync();
        _controlPrint.Antes = null;

        await using var db = _servicio.Base();
        var cobradoUno = await db.Set<TrabajoControlPrintCobrado>().AsNoTracking().Where(x => x.TrabajoId == 1 && x.LiberadoUtc == null).ToListAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict && cobradoUno.Count == 0,
            $"Control Print contestó a tiempo que el #1 ya se cobró allá (Ana); solo tardó la búsqueda del #2 (búsquedas: {string.Join(", ", pedidas)}). "
            + $"El cobro contestó {(int)respuesta.StatusCode} en {tardo.TotalSeconds:0.00} s y el #1 quedó cobrado aquí {cobradoUno.Count} vez "
            + $"(sin revisar: {cobradoUno.FirstOrDefault()?.SinRevisar}): {texto}");
        Assert.Empty(_controlPrint.Escrituras);
    }

    /// <summary>
    /// Comprobación: «lo último que se leyó» es la última lectura de toda la cola que salió bien, no la última espera de cambios que dijo
    /// «nada cambió». Adaptada a la segunda revisión del PR #146: la cola ya se relee cada 20 s aunque Control Print no avise nada, así
    /// que esa lectura solo envejece si las búsquedas dejan de contestar (aunque su espera de cambios siga contestando). Pasados 10
    /// minutos así (la hora del servicio avanza), no se cobra «sin revisar».
    /// </summary>
    [Fact]
    public async Task Comprobacion_sin_una_lectura_de_la_cola_en_10_minutos_no_se_cobra_sin_revisar()
    {
        _controlPrint.CambiarTrabajos(t => t.Add(Trabajo(1234, Hace(4), precio: 15)));
        var c = await AbrirAsync();
        await HastaQueAsync(c, x => x.Trabajos.Count == 1, "la tarjeta #1234");

        // Desde ahora sus búsquedas no contestan (su espera de cambios sí) y pasan 11 minutos: ni la cola ni la revisión leen nada.
        _controlPrint.Antes = (ctx, p) =>
        {
            if (p.Ruta != "/api/jobs/search")
                return Task.FromResult(false);
            ctx.Abort();
            return Task.FromResult(true);
        };
        _reloj.Avanzar(TimeSpan.FromMinutes(11));
        var respuesta = await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, new ImpresionDeControlPrintSolicitada(1234, 1500)));
        var texto = await respuesta.Content.ReadAsStringAsync();
        _controlPrint.Antes = null;

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Contains("Control Print no contesta: cobra #1234 cuando conteste (no se ha leído en los últimos 10 minutos)", texto);
        await using var db = _servicio.Base();
        Assert.False(await db.Set<TrabajoControlPrintCobrado>().AnyAsync(x => x.TrabajoId == 1234));
        Assert.Empty(_controlPrint.Escrituras);
    }

    /// <summary>
    /// Control Print 1.12 no avisa a sus PC el «Cobrar» de una sola tarjeta (solo los lotes): aquí el falso cambia el cobro sin subir su
    /// contador, como Control Print. La cola se vuelve a leer cada 20 s aunque no avise nada, y lo cobrado aquí y allá se nota en unos
    /// segundos (antes, hasta que pasara otra cosa allá).
    /// </summary>
    [Fact]
    public async Task Lo_cobrado_aqui_que_la_caja_principal_cobra_con_su_boton_Cobrar_se_nota_en_unos_segundos()
    {
        _controlPrint.CambiarTrabajos(t => t.Add(Trabajo(1234, Hace(4), precio: 15, documento: "Tesis.pdf")));
        var c = await AbrirAsync();
        await HastaQueAsync(c, x => x.Trabajos.Count == 1, "la tarjeta #1234");
        await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, new ImpresionDeControlPrintSolicitada(1234, 1500))));
        var lecturas = (await EstadoAsync(c)).Lecturas;

        // En la caja principal, Ana toca «Cobrar» en la tarjeta #1234 de Control Print (una sola, no un lote).
        var x = _controlPrint.Trabajos.Single(j => j.Id == 1234);
        (x.Payment, x.PaymentBy, x.PaymentUtc) = (PaymentState.Paid, "Ana", DateTime.UtcNow);

        // «En unos segundos la caja 2 lo avisa»: se le dan 25 s (dos esperas de cambios de 10 s).
        var limite = DateTime.UtcNow.AddSeconds(25);
        ColaControlPrintDto cola;
        while ((cola = await ColaAsync(c)).CobradasDosVeces is not { Count: > 0 } && DateTime.UtcNow < limite)
            await Task.Delay(200);
        var estado = await EstadoAsync(c);
        Assert.True(cola.CobradasDosVeces is { Count: > 0 },
            $"25 s después de que la caja principal lo cobró, la caja 2 no dice nada: la cola no se volvió a leer (lecturas {lecturas} → {estado.Lecturas}; "
            + $"esperas de cambios contestadas: {_controlPrint.Pedidas("/api/changes")}).");
        Assert.Empty(_controlPrint.Escrituras);
    }

    /// <summary>
    /// Lo cobrado aquí sigue «por cobrar» en Control Print para siempre. Si la caja principal lo cobra después de los días que ve la caja 2
    /// (ControlPrint:Dias, 2: hoy y ayer), también se nota: lo de antes se revisa cada 15 minutos (la hora del servicio), hasta 30 días.
    /// </summary>
    [Fact]
    public async Task Lo_cobrado_aqui_que_la_caja_principal_cobra_pasados_los_dias_que_se_ven_tambien_se_nota()
    {
        _controlPrint.CambiarTrabajos(t => t.Add(Trabajo(1234, Hace(4), precio: 15, documento: "Tesis.pdf")));
        var c = await AbrirAsync();
        await HastaQueAsync(c, x => x.Trabajos.Count == 1, "la tarjeta #1234");
        var ticket = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, new ImpresionDeControlPrintSolicitada(1234, 1500))));

        // Dos días después (la hora del servicio): allá sigue «por cobrar». En la caja principal Ana lo cobra.
        _reloj.Avanzar(TimeSpan.FromDays(2));
        var lecturas = (await EstadoAsync(c)).Lecturas;
        _controlPrint.CambiarTrabajos(t =>
        {
            var x = t.Single(j => j.Id == 1234);
            (x.Payment, x.PaymentBy, x.PaymentUtc) = (PaymentState.Paid, "Ana", DateTime.UtcNow);
        });
        // Y otro trabajo nuevo, para que la cola lea otra vez.
        await HastaLecturasAsync(c, lecturas + 1);
        _controlPrint.CambiarTrabajos(t => t.Add(Trabajo(1300, DateTime.UtcNow.AddDays(2).AddMinutes(-1), precio: 2)));
        await HastaLecturasAsync(c, lecturas + 2);

        var cola = await ColaAsync(c);
        Assert.True(cola.CobradasDosVeces is { Count: > 0 },
            $"El #1234 se cobró aquí (venta {ticket.Folio}) y dos días después en Control Print (Ana): nadie lo dice "
            + $"({(await EstadoAsync(c)).Lecturas - lecturas} lecturas después; avisos: {string.Join(" | ", cola.CobradasDosVeces ?? [])}).");
        Assert.Empty(_controlPrint.Escrituras);
    }

    /// <summary>
    /// Antes de este PR, las copias del panel registradas en F2 iban en la única línea de impresiones y la migración la deja con
    /// EsCopias = 0. Devolver completa esa línea suelta todo lo que se cobró con ella, también las copias.
    /// </summary>
    [Fact]
    public async Task Devolver_la_linea_de_impresiones_de_una_venta_de_antes_suelta_tambien_sus_copias_registradas()
    {
        var c = await AbrirAsync();
        var copias = await Leer<ImpresionPorCobrarDto>(await c.Http.PostAsJsonAsync("api/impresiones/copias",
            new SolicitudCopias(Guid.NewGuid(), c.Datos.Id, c.Cajero.UsuarioId, 2, false, "Carta", false, 400, null)));
        var ticket = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId, [],
            [new CobroSolicitado(FormaPago.Efectivo, 1000)], VentaId: Guid.NewGuid(), Impresiones: [copias.Id])));

        // Así queda una venta cobrada antes de este PR: una sola línea «Impresiones» con las copias, y EsCopias = 0 (el valor que pone la
        // migración a lo que ya estaba).
        Guid lineaId;
        long cantidad;
        await using (var db = _servicio.Base())
        {
            var linea = await db.VentasLineas.SingleAsync(l => l.VentaId == ticket.VentaId && l.EsImpresiones);
            (lineaId, cantidad) = (linea.Id, linea.Cantidad.Milesimas);
            await db.Database.ExecuteSqlRawAsync("UPDATE \"VentasLineas\" SET \"EsCopias\" = 0, \"Descripcion\" = 'Impresiones' WHERE \"Id\" = {0}", lineaId);
        }
        string PorCobrarUrl() => $"api/sucursales/{c.Datos.SucursalId}/impresiones/por-cobrar?usuarioId={c.Cajero.UsuarioId}";
        Assert.DoesNotContain((await c.Http.GetFromJsonAsync<List<ImpresionPorCobrarDto>>(PorCobrarUrl()))!, t => t.Id == copias.Id);

        // Después de actualizar, el cliente regresa y se le devuelve completa esa línea.
        await Leer<object>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
            new SolicitudDevolucion(c.Dueno.UsuarioId, c.Datos.Id, "No eran", [new LineaDevolucionSolicitada(lineaId, cantidad)])));

        var porCobrar = (await c.Http.GetFromJsonAsync<List<ImpresionPorCobrarDto>>(PorCobrarUrl()))!;
        await using (var db = _servicio.Base())
        {
            var devuelta = await db.VentasLineas.AsNoTracking().SingleAsync(l => l.Id == lineaId);
            Assert.True(porCobrar.Any(t => t.Id == copias.Id),
                $"La línea se devolvió completa ({devuelta.CantidadDevuelta} de {devuelta.Cantidad}) y sus copias registradas siguen cobradas: "
                + $"no vuelven a estar por cobrar (por cobrar: {porCobrar.Count}).");
        }
    }

    /// <summary>
    /// Lo cobrado aquí que allá sigue «Pending» pero ya no es cobrable (se cobró aquí mientras se imprimía y después se canceló en la cola:
    /// «Cancelled») queda revisado, sin cobro doble, y no se vuelve a buscar en cada lectura de la cola.
    /// </summary>
    [Fact]
    public async Task Lo_cobrado_aqui_que_alla_se_cancela_no_se_busca_en_cada_lectura()
    {
        var hora = Hace(4);
        _controlPrint.CambiarTrabajos(t => t.Add(Trabajo(1234, hora, precio: 15, estado: JobState.Analyzed)));
        var c = await AbrirAsync();
        await HastaQueAsync(c, x => x.Trabajos.Count == 1, "la tarjeta #1234");
        await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, new ImpresionDeControlPrintSolicitada(1234, 1500))));

        // Allá se cancela en la cola (sigue «Pending»); después, cinco cambios cualquiera en Control Print.
        var lecturas = (await EstadoAsync(c)).Lecturas;
        _controlPrint.CambiarTrabajos(t => t.Single(x => x.Id == 1234).State = JobState.Cancelled);
        await HastaLecturasAsync(c, lecturas + 1);
        var antes = _controlPrint.Busquedas.Count;
        for (var i = 0; i < 5; i++)
        {
            _controlPrint.CambiarTrabajos(t => t.Add(Trabajo(2000 + i, Hace(1), precio: 2)));
            await HastaLecturasAsync(c, lecturas + 2 + i);
        }

        var delCobrado = _controlPrint.Busquedas.Skip(antes).Count(q => q is { FromUtc: { } desde, ToUtc: { } hasta }
            && hora >= desde.ToUniversalTime() && hora < hasta.ToUniversalTime());
        Assert.True(delCobrado == 0, $"En 5 lecturas de la cola, el #1234 (cobrado aquí, cancelado allá) se volvió a buscar {delCobrado} veces.");
    }

    /// <summary>
    /// El dueño tiene un chat vinculado y apagó los avisos de la impresora. Un cobro doble es dinero, no la impresora: va con su propio tipo
    /// de aviso (TipoAlerta.CobradaDosVeces) y le llega.
    /// </summary>
    [Fact]
    public async Task Lo_cobrado_dos_veces_le_llega_al_dueno_aunque_apague_los_avisos_de_la_impresora()
    {
        var c = await AbrirAsync();
        await AvisosDelDuenoAsync(c, apagadas: "Impresora");
        await CobradaDosVecesAsync(c);

        await using var db = _servicio.Base();
        var mensajes = await db.MensajesTelegram.AsNoTracking().Where(m => m.Texto.Contains("se cobró dos veces")).ToListAsync();
        Assert.True(mensajes.Count == 1,
            $"F2 dice que el #1234 se cobró dos veces, pero al dueño le llegaron {mensajes.Count} avisos (tiene apagados solo los de la impresora).");
    }

    /// <summary>Comprobación: con sus avisos prendidos le llega uno solo, aunque la cola se lea muchas veces más.</summary>
    [Fact]
    public async Task Comprobacion_lo_cobrado_dos_veces_se_avisa_una_sola_vez()
    {
        var c = await AbrirAsync();
        await AvisosDelDuenoAsync(c, apagadas: "");
        await CobradaDosVecesAsync(c);
        var lecturas = (await EstadoAsync(c)).Lecturas;
        for (var i = 0; i < 5; i++)
        {
            _controlPrint.CambiarTrabajos(t => t.Add(Trabajo(2000 + i, Hace(1), precio: 2)));
            await HastaLecturasAsync(c, lecturas + i + 1);
        }

        await using var db = _servicio.Base();
        var mensajes = await db.MensajesTelegram.AsNoTracking().Where(m => m.Texto.Contains("se cobró dos veces")).ToListAsync();
        Assert.Single(mensajes);
        Assert.Single((await ColaAsync(c)).CobradasDosVeces!);
        Assert.Single(_registro.Lineas, l => l.Nivel == LogLevel.Warning && l.Texto.Contains("se cobró dos veces"));
    }

    private async Task AvisosDelDuenoAsync(Caja c, string apagadas)
    {
        await using var db = _servicio.Base();
        db.ChatsTelegram.Add(new ChatTelegram { UsuarioId = c.Dueno.UsuarioId, ChatId = 4242, Nombre = "Dueño", VinculadoUtc = DateTime.UtcNow });
        if (await db.ConfiguracionesSucursal.FindAsync(c.Datos.SucursalId) is { } configuracion)
            configuracion.AlertasApagadas = apagadas;
        else
            db.ConfiguracionesSucursal.Add(new PapeleriaPos.Nucleo.Configuracion.ConfiguracionSucursal
            {
                SucursalId = c.Datos.SucursalId, AlertasApagadas = apagadas,
            });
        await db.SaveChangesAsync();
    }

    /// <summary>El #1234 se cobra aquí y después en Control Print (Ana): hasta que F2 lo dice.</summary>
    private async Task CobradaDosVecesAsync(Caja c)
    {
        _controlPrint.CambiarTrabajos(t => t.Add(Trabajo(1234, Hace(4), precio: 15, documento: "Tesis.pdf")));
        await HastaQueAsync(c, x => x.Trabajos.Any(t => t.Id == 1234), "la tarjeta #1234");
        await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, new ImpresionDeControlPrintSolicitada(1234, 1500))));
        _controlPrint.CambiarTrabajos(t =>
        {
            var x = t.Single(j => j.Id == 1234);
            (x.Payment, x.PaymentBy, x.PaymentUtc) = (PaymentState.Paid, "Ana", DateTime.UtcNow);
        });
        await HastaQueAsync(c, x => x.CobradasDosVeces is { Count: > 0 }, "que diga que se cobró dos veces");
    }

    /// <summary>
    /// Comprobación: sobre una copia de una base con datos (30 días ficticios y una venta con su línea de copias), regresar
    /// 20261029151304_TrabajosControlPrintCobrados quita EsCopias con su DROP COLUMN sin perder ni cambiar ningún renglón de VentasLineas,
    /// quita sus dos tablas, y volver a aplicarla deja EsCopias en 0.
    /// </summary>
    [Fact]
    public async Task Comprobacion_regresar_la_migracion_no_pierde_lineas_de_venta()
    {
        var c = await AbrirAsync();
        var copias = await Leer<ImpresionPorCobrarDto>(await c.Http.PostAsJsonAsync("api/impresiones/copias",
            new SolicitudCopias(Guid.NewGuid(), c.Datos.Id, c.Cajero.UsuarioId, 2, false, "Carta", false, 400, null)));
        await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId, [],
            [new CobroSolicitado(FormaPago.Efectivo, 1000)], VentaId: Guid.NewGuid(), Impresiones: [copias.Id])));
        var copia = Path.Combine(Path.GetTempPath(), $"rev146b-migracion-{Guid.NewGuid():N}.db");
        try
        {
            await using (var db = _servicio.Base())
                await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{copia}'");
            const string Lineas = """
                SELECT COUNT(*) || '|' || SUM("ImporteCentavos") || '|' || SUM("CostoCentavos") || '|' || SUM("EsImpresiones") || '|'
                    || group_concat(COALESCE("Id", '-') || ':'
                        || COALESCE("AhorroMayoreoCentavos", '-') || ':'
                        || COALESCE("AhorroPromocionCentavos", '-') || ':'
                        || COALESCE("AutorizacionPrecioId", '-') || ':'
                        || COALESCE("Cantidad", '-') || ':'
                        || COALESCE("CantidadBase", '-') || ':'
                        || COALESCE("CantidadDevuelta", '-') || ':'
                        || COALESCE("CostoCentavos", '-') || ':'
                        || COALESCE("Descripcion", '-') || ':'
                        || COALESCE("DescuentoCentavos", '-') || ':'
                        || COALESCE("DescuentoCentesimas", '-') || ':'
                        || COALESCE("EsImpresiones", '-') || ':'
                        || COALESCE("ImporteCentavos", '-') || ':'
                        || COALESCE("Impuesto", '-') || ':'
                        || COALESCE("IvaCentavos", '-') || ':'
                        || COALESCE("LlevaInventario", '-') || ':'
                        || COALESCE("MayoreoDesde", '-') || ':'
                        || COALESCE("MotivoDescuento", '-') || ':'
                        || COALESCE("PrecioCentavos", '-') || ':'
                        || COALESCE("PrecioOriginalCentavos", '-') || ':'
                        || COALESCE("PresentacionId", '-') || ':'
                        || COALESCE("Promocion", '-') || ':'
                        || COALESCE("PromocionId", '-') || ':'
                        || COALESCE("Unidad", '-') || ':'
                        || COALESCE("VarianteId", '-') || ':'
                        || COALESCE("VentaId", '-'), ',')
                FROM (SELECT * FROM "VentasLineas" ORDER BY "Id")
                """;
            await using var conexion = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copia};Pooling=False");
            await conexion.OpenAsync();
            string Valor(string sql)
            {
                using var cmd = conexion.CreateCommand();
                cmd.CommandText = sql;
                return Convert.ToString(cmd.ExecuteScalar())!;
            }
            var antes = Valor(Lineas);
            Assert.Equal("1", Valor("SELECT COUNT(*) FROM \"VentasLineas\" WHERE \"EsCopias\" = 1"));
            await using var ctx = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(conexion).Options);
            var migrador = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(ctx);

            migrador.Migrate("20261025221148_UsuariosDeEleventa");
            Assert.Equal("0", Valor("SELECT COUNT(*) FROM pragma_table_info('VentasLineas') WHERE name = 'EsCopias'"));
            Assert.Equal("0", Valor("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('TrabajosControlPrintCobrados', 'HistorialCostosImpresion')"));
            Assert.Equal(antes, Valor(Lineas));

            migrador.Migrate();
            Assert.Equal(antes, Valor(Lineas));
            Assert.Equal("0", Valor("SELECT COUNT(*) FROM \"VentasLineas\" WHERE \"EsCopias\" <> 0"));
            Assert.Equal("ok", Valor("PRAGMA integrity_check"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(copia);
        }
    }

    /// <summary>
    /// Comprobación: lo cobrado «sin revisar» mientras Control Print no contestaba, y que la caja principal cobró en ese rato, se nota cuando
    /// Control Print vuelve.
    /// </summary>
    [Fact]
    public async Task Comprobacion_lo_cobrado_sin_revisar_que_alla_se_cobro_en_ese_rato_se_nota_al_volver()
    {
        _controlPrint.CambiarTrabajos(t => t.Add(Trabajo(1234, Hace(4), precio: 15, documento: "Tesis.pdf")));
        var c = await AbrirAsync();
        await HastaQueAsync(c, x => x.Trabajos.Count == 1, "la tarjeta #1234");
        _controlPrint.Antes = (ctx, _) =>
        {
            ctx.Abort();
            return Task.FromResult(true);
        };
        _controlPrint.Avisar(0);
        await HastaQueAsync(c, x => x.Contesta == false, "que diga que no contesta");
        await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, new ImpresionDeControlPrintSolicitada(1234, 1500))));
        // Mientras, en la caja principal, Ana lo cobra en Control Print.
        _controlPrint.CambiarTrabajos(t =>
        {
            var x = t.Single(j => j.Id == 1234);
            (x.Payment, x.PaymentBy, x.PaymentUtc) = (PaymentState.Paid, "Ana", DateTime.UtcNow);
        });

        _controlPrint.Antes = null;
        _controlPrint.Avisar(0);
        var cola = await HastaQueAsync(c, x => x.CobradasDosVeces is { Count: > 0 }, "que diga que se cobró dos veces");

        Assert.StartsWith("#1234 (Tesis.pdf) se cobró dos veces", Assert.Single(cola.CobradasDosVeces!));
        await using var db = _servicio.Base();
        var cobrado = await db.Set<TrabajoControlPrintCobrado>().AsNoTracking().SingleAsync(x => x.TrabajoId == 1234);
        Assert.Equal((false, "Ana"), (cobrado.SinRevisar, cobrado.CobradoEnControlPrintPor));
        Assert.Empty(_controlPrint.Escrituras);
    }

    /// <summary>
    /// Comprobación, sin falsas alarmas: lo devuelto aquí que después se cobra allá, lo que allá se marca «no vendida» y lo que allá se borra
    /// no se avisan como cobrados dos veces; los dos últimos quedan revisados (no se vuelven a buscar).
    /// </summary>
    [Fact]
    public async Task Comprobacion_lo_devuelto_aqui_lo_no_vendido_y_lo_borrado_alla_no_se_avisan()
    {
        _controlPrint.CambiarTrabajos(t => t.AddRange([Trabajo(1, Hace(6), precio: 4), Trabajo(2, Hace(5), precio: 4), Trabajo(3, Hace(4), precio: 4)]));
        var c = await AbrirAsync();
        await HastaQueAsync(c, x => x.Trabajos.Count == 3, "las tres tarjetas");
        var tickets = new List<TicketDto>();
        foreach (var id in new long[] { 1, 2, 3 })
            tickets.Add(await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, new ImpresionDeControlPrintSolicitada(id, 400)))));
        // El #1 se devuelve aquí completo.
        await using (var db = _servicio.Base())
        {
            var linea = await db.VentasLineas.SingleAsync(l => l.VentaId == tickets[0].VentaId && l.EsImpresiones);
            await Leer<object>(await c.Http.PostAsJsonAsync($"api/ventas/{tickets[0].VentaId}/devoluciones",
                new SolicitudDevolucion(c.Dueno.UsuarioId, c.Datos.Id, "No era", [new LineaDevolucionSolicitada(linea.Id, linea.Cantidad.Milesimas)])));
        }
        var lecturas = (await EstadoAsync(c)).Lecturas;
        // Allá: el #1 se cobra (ya se le devolvió aquí), el #2 se marca no vendida y el #3 se borra.
        _controlPrint.CambiarTrabajos(t =>
        {
            var uno = t.Single(j => j.Id == 1);
            (uno.Payment, uno.PaymentBy, uno.PaymentUtc) = (PaymentState.Paid, "Ana", DateTime.UtcNow);
            t.Single(j => j.Id == 2).Payment = PaymentState.Unsold;
            t.RemoveAll(j => j.Id == 3);
        });
        await HastaLecturasAsync(c, lecturas + 1);
        _controlPrint.CambiarTrabajos(t => t.Add(Trabajo(9, Hace(1), precio: 2)));
        await HastaLecturasAsync(c, lecturas + 2);

        Assert.Empty((await ColaAsync(c)).CobradasDosVeces ?? []);
        Assert.Empty((await EstadoAsync(c)).Avisos ?? []);
        await using var revisar = _servicio.Base();
        var filas = await revisar.Set<TrabajoControlPrintCobrado>().AsNoTracking().OrderBy(x => x.TrabajoId).ToListAsync();
        Assert.Equal([(1L, true, false), (2L, false, true), (3L, false, true)],
            filas.Select(x => (x.TrabajoId, x.LiberadoUtc is not null, x.RevisadoUtc is not null)));
        Assert.All(filas, x => Assert.Null(x.CobradoEnControlPrintUtc));
        Assert.Empty(_controlPrint.Escrituras);
    }

    /// <summary>
    /// Comprobación: con Control Print sin contestar, dos tickets que cobran a la vez la misma tarjeta «sin revisar» dejan una sola venta
    /// (el índice único de TrabajosControlPrintCobrados también cubre ese camino).
    /// </summary>
    [Fact]
    public async Task Comprobacion_dos_cobros_sin_revisar_a_la_vez_de_la_misma_tarjeta_dejan_una_venta()
    {
        _controlPrint.CambiarTrabajos(t => t.Add(Trabajo(1234, Hace(4), precio: 15)));
        var c = await AbrirAsync();
        await HastaQueAsync(c, x => x.Trabajos.Count == 1, "la tarjeta #1234");
        _controlPrint.Antes = async (_, p) =>
        {
            if (p.Ruta == "/api/jobs/search")
                await Task.Delay(TimeSpan.FromSeconds(1));
            return false;
        };

        var respuestas = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            c.Http.PostAsJsonAsync("api/ventas", Cobro(c, new ImpresionDeControlPrintSolicitada(1234, 1500)))));
        _controlPrint.Antes = null;

        var estados = respuestas.Select(r => r.StatusCode).OrderBy(s => (int)s).ToList();
        var textos = await Task.WhenAll(respuestas.Select(r => r.Content.ReadAsStringAsync()));
        Assert.True(estados[0] == HttpStatusCode.OK && estados[1] is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
            $"{string.Join(" / ", estados)}: {string.Join(" / ", textos)}");
        await using var db = _servicio.Base();
        var cobrados = await db.Set<TrabajoControlPrintCobrado>().AsNoTracking().Where(x => x.TrabajoId == 1234 && x.LiberadoUtc == null).ToListAsync();
        Assert.True(cobrados is [{ SinRevisar: true }], $"{cobrados.Count} cobrados (sin revisar: {string.Join(",", cobrados.Select(x => x.SinRevisar))})");
        Assert.Equal(1, await db.Ventas.CountAsync(v => v.TurnoId == c.Turno.Id));
        Assert.Empty(_controlPrint.Escrituras);
    }
}
