using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PapeleriaPos.Impresiones;
using PapeleriaPos.Impresiones.Contratos;
using PapeleriaPos.Impresiones.ControlPrint;
using PapeleriaPos.Impresiones.Medicion;
using PapeleriaPos.Impresiones.Pruebas.ControlPrint;
using PapeleriaPos.Nucleo.Contratos;
using PapeleriaPos.Nucleo.Ventas;
using static PapeleriaPos.Impresiones.Pruebas.ControlPrint.PorCobrarDePrueba;

namespace PapeleriaPos.Pruebas.Servicio;

/// <summary>
/// Las tarjetas de la cola de Control Print en la caja 2 (2026-10-25), por el servicio de verdad contra el Control Print falso: la
/// cola llega a la caja sin lo que ya se cobró aquí, cobrarlas las recuerda (allá siguen «por cobrar»), antes de cobrar se revisan
/// (lo cobrado en la caja principal no se cobra dos veces), cancelar o devolver las vuelve a mostrar, lo que imprime la caja 2 se
/// cobra con los costos de Control Print y, si no contesta, nada detiene la venta. Nunca se le escribe: el falso falla si pasa.
/// </summary>
public sealed class ControlPrintEnLaCajaTests : IAsyncLifetime
{
    private ServidorControlPrintFalso _controlPrint = null!;
    private ServicioDePrueba _servicio = null!;

    public async Task InitializeAsync()
    {
        _controlPrint = await ServidorControlPrintFalso.IniciarAsync(Codigo);
        _servicio = await ServicioDePrueba.CrearAsync(Ajustes(_controlPrint));
    }

    public async Task DisposeAsync()
    {
        await _servicio.DisposeAsync();
        // Al terminar, el falso hace fallar la prueba si alguien le escribió.
        await _controlPrint.DisposeAsync();
    }

    /// <summary>
    /// La sección «ControlPrint» del servicio de la caja 2. El código, como en la tienda, en la variable de entorno del servicio
    /// (ControlPrint__Codigo): el servicio no lo toma de otro lado (2026-10-29). Es el mismo para todas las pruebas, así que se pone una
    /// vez para todo el proceso.
    /// </summary>
    internal static (string, string)[] Ajustes(ServidorControlPrintFalso controlPrint)
    {
        Environment.SetEnvironmentVariable(PapeleriaPos.Impresiones.Servicio.ServiciosImpresiones.VariableCodigo, Codigo);
        return [("ControlPrint:Servidor", "127.0.0.1"), ("ControlPrint:Puerto", controlPrint.Puerto.ToString())];
    }

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private sealed record Caja(HttpClient Http, CajaDto Datos, SesionDto Cajero, SesionDto Dueno, TurnoDto Turno);

    private async Task<Caja> AbrirAsync()
    {
        var http = _servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var dueno = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("dueno", "3333")));
        var turno = await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, cajero.UsuarioId, 50000)));
        return new Caja(http, caja, cajero, dueno, turno);
    }

    private static async Task<ColaControlPrintDto> ColaAsync(Caja c, SesionDto? quien = null) =>
        await Leer<ColaControlPrintDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/impresiones/control-print?usuarioId={(quien ?? c.Cajero).UsuarioId}"));

    private static async Task<ResumenPorCobrarDto> ResumenAsync(Caja c) =>
        await Leer<ResumenPorCobrarDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/impresiones/por-cobrar/cuantas"));

    /// <summary>Hasta que la cola de la caja diga lo que se espera (el servicio la lee en segundo plano).</summary>
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

    private void Agregar(params JobRecordCp[] trabajos) => _controlPrint.CambiarTrabajos(t => t.AddRange(trabajos));

    private static SolicitudCobro Cobro(Caja c, IReadOnlyList<ImpresionDeControlPrintSolicitada> controlPrint, IReadOnlyList<Guid>? delModulo = null) =>
        new(c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId, [], [new CobroSolicitado(FormaPago.Efectivo, 100000)], VentaId: Guid.NewGuid(),
            Impresiones: delModulo, ImpresionesControlPrint: controlPrint);

    // ── La cola en la caja ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task La_cola_de_Control_Print_llega_a_la_caja_como_sus_tarjetas_y_solo_se_lee()
    {
        var conMiniatura = Trabajo(1234, Hace(4), precio: 15, documento: "Tesis.pdf");
        conMiniatura.Thumbnail = DatosDeControlPrint.Png;
        Agregar(Trabajo(1230, Hace(40), precio: 2), conMiniatura, Trabajo(1231, Hace(30), cobro: PaymentState.Paid));
        var c = await AbrirAsync();

        var cola = await HastaQueAsync(c, x => x.Trabajos.Count == 2, "las dos por cobrar");

        Assert.True(cola.Configurado);
        Assert.True(cola.Contesta);
        Assert.Equal("PRINCIPAL", cola.Servidor);
        Assert.Equal([1234L, 1230], cola.Trabajos.Select(t => t.Id));
        var tarjeta = cola.Trabajos[0];
        Assert.Equal(("Tesis.pdf", "2 págs · B/N · Carta · Papel normal", "PRINCIPAL", "Sin sesión", 1500L),
            (tarjeta.Titulo, tarjeta.Resumen, tarjeta.Pc, tarjeta.Atendio, tarjeta.PrecioCentavos));
        // El costo solo a quien lo ve.
        Assert.Null(tarjeta.CostoCentavos);
        Assert.Equal(112L, (await ColaAsync(c, c.Dueno)).Trabajos[0].CostoCentavos);

        // Con la misma versión no se vuelve a mandar la lista: nada que pintar.
        var otraVez = await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/impresiones/control-print?usuarioId={c.Cajero.UsuarioId}&version={cola.Version}");
        Assert.Equal(HttpStatusCode.NoContent, otraVez.StatusCode);
        // El resumen del chip de arriba, sin las listas.
        var resumen = await ResumenAsync(c);
        Assert.Equal((2, cola.Version, true, (bool?)true), (resumen.DeControlPrint, resumen.VersionControlPrint, resumen.ControlPrintConfigurado, resumen.ControlPrintContesta));

        // La miniatura, cuando la caja la va a mostrar: una vez.
        var miniatura = await c.Http.GetAsync("api/impresiones/control-print/1234/miniatura");
        Assert.Equal("image/png", miniatura.Content.Headers.ContentType?.MediaType);
        Assert.Equal(DatosDeControlPrint.Png, await miniatura.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await c.Http.GetAsync("api/impresiones/control-print/1230/miniatura")).StatusCode);
        await c.Http.GetAsync("api/impresiones/control-print/1234/miniatura");
        Assert.Equal(1, _controlPrint.Pedidas("/api/jobs/1234/thumbnail"));

        // Cómo lo ve el servicio (lo que lee poner-codigo-controlprint.ps1): sin el código.
        var texto = await c.Http.GetStringAsync("api/controlprint");
        var estado = JsonSerializer.Deserialize<EstadoControlPrintDto>(texto, JsonSerializerOptions.Web)!;
        Assert.Equal((true, true, (bool?)true, "PRINCIPAL", "1.12.0"), (estado.Configurado, estado.TieneCodigo, estado.Contesta, estado.Nombre, estado.Version));
        Assert.Equal(ProtocoloControlPrintLegible(_controlPrint.Huella), estado.Huella);
        Assert.False(estado.HuellaConfigurada);
        Assert.DoesNotContain("K7QP", texto);
        // La huella quedó fijada junto a la base, para la próxima vez que arranque.
        Assert.True(File.Exists(Path.ChangeExtension(_servicio.Ruta, ".controlprint")));
        Assert.Empty(_controlPrint.Escrituras);
    }

    private static string ProtocoloControlPrintLegible(string huella) => PapeleriaPos.Impresiones.ControlPrint.ProtocoloControlPrint.HuellaLegible(huella);

    // ── Cobrarlas ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cobrarlas_las_recuerda_aqui_sin_marcarlas_en_Control_Print_y_ya_no_salen()
    {
        Agregar(Trabajo(1234, Hace(4), precio: 15), Trabajo(1235, Hace(3), precio: 2));
        var c = await AbrirAsync();
        await HastaQueAsync(c, x => x.Trabajos.Count == 2, "las dos por cobrar");

        var ticket = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas",
            Cobro(c, [new ImpresionDeControlPrintSolicitada(1234, 1500), new ImpresionDeControlPrintSolicitada(1235, 200)])));

        // Una sola línea, con qué trabajos son (fuera del piloto, como siempre: una pieza por el monto).
        var linea = Assert.Single(ticket.Lineas);
        Assert.Equal(("Impresiones #1234, #1235", 1700L), (linea.Descripcion, linea.ImporteCentavos));
        Assert.Equal(1700, ticket.TotalCentavos);
        await using (var db = _servicio.Base())
        {
            var venta = await db.Ventas.Include(v => v.Lineas).SingleAsync(v => v.Id == ticket.VentaId);
            Assert.Equal(224, venta.Lineas.Single().CostoCentavos);
            var cobrados = await db.Set<TrabajoControlPrintCobrado>().OrderBy(x => x.TrabajoId).ToListAsync();
            Assert.Equal([(1234L, 1500L), (1235L, 200L)], cobrados.Select(x => (x.TrabajoId, x.PrecioCentavos)));
            Assert.All(cobrados, x => Assert.Equal((ticket.VentaId!.Value, (DateTime?)null), (x.VentaId, x.LiberadoUtc)));
        }
        // En Control Print siguen «por cobrar» (nunca se le escribe), pero aquí ya no salen.
        Assert.All(_controlPrint.Trabajos, t => Assert.Equal(PaymentState.Pending, t.Payment));
        Assert.Empty((await ColaAsync(c)).Trabajos);
        Assert.Equal(0, (await ResumenAsync(c)).DeControlPrint);

        // Otro ticket con la misma tarjeta (abierta antes en otra ventana): no se cobra dos veces.
        var otra = await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, [new ImpresionDeControlPrintSolicitada(1234, 1500)]));
        Assert.Equal(HttpStatusCode.Conflict, otra.StatusCode);
        var cambio = (await otra.Content.ReadFromJsonAsync<ControlPrintAlCobrarDto>())!;
        Assert.Equal([1234L], cambio.Quitar);
        Assert.Equal(ControlPrintAlCobrarDto.CodigoDelError, cambio.Codigo);
        Assert.Contains("#1234 ya se cobró en esta caja", cambio.Mensaje);
        Assert.Empty(_controlPrint.Escrituras);
    }

    [Fact]
    public async Task Lo_que_ya_se_cobro_en_la_caja_principal_no_se_cobra_dos_veces()
    {
        Agregar(Trabajo(1234, Hace(4), precio: 15), Trabajo(1235, Hace(3), precio: 2), Trabajo(1236, Hace(2), precio: 6));
        var c = await AbrirAsync();
        await HastaQueAsync(c, x => x.Trabajos.Count == 3, "las tres por cobrar");
        // Mientras el cliente espera en la caja 2, en la caja principal cobran el 1234 y le ponen precio especial al 1236.
        _controlPrint.CambiarTrabajos(t =>
        {
            var cobrado = t.Single(x => x.Id == 1234);
            (cobrado.Payment, cobrado.PaymentBy) = (PaymentState.Paid, "Ana");
            var especial = t.Single(x => x.Id == 1236);
            (especial.PriceOverride, especial.PriceKind, especial.SpecialName) = (5, PriceKind.Special, "Escuela");
        });

        var respuesta = await c.Http.PostAsJsonAsync("api/ventas", Cobro(c,
            [new ImpresionDeControlPrintSolicitada(1234, 1500), new ImpresionDeControlPrintSolicitada(1235, 200), new ImpresionDeControlPrintSolicitada(1236, 600)]));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var cambio = (await respuesta.Content.ReadFromJsonAsync<ControlPrintAlCobrarDto>())!;
        Assert.Equal([1234L], cambio.Quitar);
        Assert.Equal([new ImpresionDeControlPrintSolicitada(1236, 500)], cambio.Precios);
        Assert.StartsWith("No se cobró: #1234 ya se cobró en Control Print (Ana); a #1236 le cambiaron el precio en Control Print: ahora es $5.00.", cambio.Mensaje);
        // No se cobró nada: ni venta ni recuerdo.
        await using (var db = _servicio.Base())
        {
            Assert.False(await db.Ventas.AnyAsync(v => v.TurnoId == c.Turno.Id));
            Assert.False(await db.Set<TrabajoControlPrintCobrado>().AnyAsync());
        }

        // La caja quita el 1234, pone el precio de ahora y cobra otra vez.
        var ticket = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas",
            Cobro(c, [new ImpresionDeControlPrintSolicitada(1235, 200), new ImpresionDeControlPrintSolicitada(1236, 500)])));
        Assert.Equal(700, ticket.TotalCentavos);
        Assert.Empty(_controlPrint.Escrituras);
    }

    [Fact]
    public async Task Cancelar_la_venta_o_devolver_la_linea_las_vuelve_a_mostrar_y_se_devuelve_completa()
    {
        Agregar(Trabajo(1234, Hace(4), precio: 15), Trabajo(1235, Hace(3), precio: 2));
        var c = await AbrirAsync();
        await HastaQueAsync(c, x => x.Trabajos.Count == 2, "las dos por cobrar");
        var primera = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, [new ImpresionDeControlPrintSolicitada(1234, 1500)])));
        var segunda = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, [new ImpresionDeControlPrintSolicitada(1235, 200)])));
        Assert.Empty((await ColaAsync(c)).Trabajos);

        // Se cancela la primera: el 1234 vuelve a salir (en Control Print siguió por cobrar).
        await Leer<object>(await c.Http.PostAsJsonAsync($"api/ventas/{primera.VentaId}/cancelacion",
            new SolicitudCancelacion(c.Dueno.UsuarioId, c.Datos.Id, "El cliente ya no lo quiso")));
        Assert.Equal([1234L], (await ColaAsync(c)).Trabajos.Select(t => t.Id));

        // De la segunda: la línea de impresiones se devuelve completa; una parte, no.
        await using (var db = _servicio.Base())
        {
            var linea = await db.VentasLineas.SingleAsync(l => l.VentaId == segunda.VentaId);
            var parte = await c.Http.PostAsJsonAsync($"api/ventas/{segunda.VentaId}/devoluciones",
                new SolicitudDevolucion(c.Dueno.UsuarioId, c.Datos.Id, "Salió mal", [new LineaDevolucionSolicitada(linea.Id, 500)]));
            Assert.Equal(HttpStatusCode.BadRequest, parte.StatusCode);
            Assert.Contains("se devuelve completa", (await parte.Content.ReadFromJsonAsync<ErrorDto>())!.Mensaje);
            await Leer<object>(await c.Http.PostAsJsonAsync($"api/ventas/{segunda.VentaId}/devoluciones",
                new SolicitudDevolucion(c.Dueno.UsuarioId, c.Datos.Id, "Salió mal", [new LineaDevolucionSolicitada(linea.Id, linea.Cantidad.Milesimas)])));
        }
        Assert.Equal([1235L, 1234], (await ColaAsync(c)).Trabajos.Select(t => t.Id));
        await using (var db = _servicio.Base())
        {
            // Con su historia: cobrados y liberados, cada uno en su venta.
            var historia = await db.Set<TrabajoControlPrintCobrado>().OrderBy(x => x.TrabajoId).ToListAsync();
            Assert.Equal([(1234L, primera.VentaId!.Value), (1235L, segunda.VentaId!.Value)], historia.Select(x => (x.TrabajoId, x.VentaId)));
            Assert.All(historia, x => Assert.NotNull(x.LiberadoUtc));
        }
        // Y se pueden volver a cobrar.
        await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, [new ImpresionDeControlPrintSolicitada(1234, 1500)])));
        Assert.Equal([1235L], (await ColaAsync(c)).Trabajos.Select(t => t.Id));
    }

    // ── Sin Control Print ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Si_Control_Print_no_contesta_se_dice_y_nada_detiene_la_venta()
    {
        Agregar(Trabajo(1234, Hace(4), precio: 15));
        var c = await AbrirAsync();
        await HastaQueAsync(c, x => x.Trabajos.Count == 1, "la por cobrar");

        // La caja principal se apaga.
        _controlPrint.Antes = (ctx, _) =>
        {
            ctx.Abort();
            return Task.FromResult(true);
        };
        _controlPrint.Avisar(0);
        var cola = await HastaQueAsync(c, x => x.Contesta == false, "que diga que no contesta");
        Assert.Empty(cola.Trabajos);
        Assert.Contains("No se pudo hablar con Control Print en 127.0.0.1", cola.Aviso);
        var resumen = await ResumenAsync(c);
        Assert.Equal(((bool?)false, 0), (resumen.ControlPrintContesta, resumen.DeControlPrint));

        // Una tarjeta que ya estaba en el ticket se cobra con lo último que se leyó de ella (hace un momento) y queda «sin revisar»
        // (2026-10-29): nunca «a mano», que no la liga a su trabajo.
        var ticket = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", Cobro(c, [new ImpresionDeControlPrintSolicitada(1234, 1500)])));
        Assert.Equal(1500, ticket.TotalCentavos);
        await using (var db = _servicio.Base())
            Assert.True((await db.Set<TrabajoControlPrintCobrado>().SingleAsync(x => x.TrabajoId == 1234)).SinRevisar);

        // Lo demás también: un artículo común y lo que imprime la caja 2.
        var comun = new SolicitudCobro(c.Datos.Id, c.Turno.Id, c.Cajero.UsuarioId, [new LineaSolicitada(null, null, 1000, "Cuaderno", 1500)],
            [new CobroSolicitado(FormaPago.Efectivo, 2000)]);
        Assert.Equal(1500, (await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", comun))).TotalCentavos);
        await Leer<object>(await c.Http.PostAsJsonAsync("api/impresiones", Medido(c, "Recibo.pdf")));
        Assert.Equal(1, (await ResumenAsync(c)).DeLaSucursal);

        // Vuelve a contestar: allá sigue por cobrar (el POS nunca le escribe), así que aquí queda revisada y no se ofrece otra vez.
        _controlPrint.Antes = null;
        _controlPrint.Avisar(0);
        await HastaQueAsync(c, x => x.Contesta == true, "que vuelva a contestar");
        await HastaAsync(() =>
        {
            using var db = _servicio.Base();
            return !db.Set<TrabajoControlPrintCobrado>().Single(x => x.TrabajoId == 1234).SinRevisar;
        }, "que la revise");
        Assert.Empty((await ColaAsync(c)).Trabajos);
    }

    /// <summary>
    /// El código de conexión, solo de la variable de entorno del servicio (2026-10-29): uno en servicio.json se ignora, y el aviso dice
    /// dónde está y dónde va, sin repetirlo.
    /// </summary>
    [Fact]
    public void El_codigo_en_un_archivo_se_ignora_y_se_avisa_sin_repetirlo()
    {
        var carpeta = Path.Combine(Path.GetTempPath(), $"cp-codigo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(carpeta);
        var prefijo = $"PRUEBA_CP_{Guid.NewGuid():N}_";
        try
        {
            const string enElArchivo = "ZZZZ-YYYY-XXXX-WWWW";
            var archivo = Path.Combine(carpeta, "servicio.json");
            File.WriteAllText(archivo, $$"""{ "ControlPrint": { "Servidor": "192.168.1.106", "Codigo": "{{enElArchivo}}" } }""");
            var soloElArchivo = new Microsoft.Extensions.Configuration.ConfigurationManager();
            Microsoft.Extensions.Configuration.JsonConfigurationExtensions.AddJsonFile(soloElArchivo, archivo);

            var (codigo, aviso) = PapeleriaPos.Impresiones.Servicio.ServiciosImpresiones.CodigoDelEntorno(soloElArchivo);

            Assert.Null(codigo);
            Assert.Contains("está en servicio.json y ahí se ignora", aviso);
            Assert.Contains("ControlPrint__Codigo", aviso);
            Assert.DoesNotContain(ProtocoloControlPrint.NormalizarCodigo(enElArchivo), ProtocoloControlPrint.NormalizarCodigo(aviso!));

            // Con la variable de entorno, vale ese (y se sigue avisando del archivo).
            Environment.SetEnvironmentVariable($"{prefijo}ControlPrint__Codigo", Codigo);
            var conLaVariable = new Microsoft.Extensions.Configuration.ConfigurationManager();
            Microsoft.Extensions.Configuration.JsonConfigurationExtensions.AddJsonFile(conLaVariable, archivo);
            Microsoft.Extensions.Configuration.EnvironmentVariablesExtensions.AddEnvironmentVariables(conLaVariable, prefijo);
            (codigo, aviso) = PapeleriaPos.Impresiones.Servicio.ServiciosImpresiones.CodigoDelEntorno(conLaVariable);
            Assert.Equal(Codigo, codigo);
            Assert.NotNull(aviso);
        }
        finally
        {
            Environment.SetEnvironmentVariable($"{prefijo}ControlPrint__Codigo", null);
            Directory.Delete(carpeta, recursive: true);
        }
    }

    [Fact]
    public async Task Sin_la_seccion_ControlPrint_no_hay_tarjetas_como_antes()
    {
        await using var sinControlPrint = await ServicioDePrueba.CrearAsync();
        var http = sinControlPrint.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var cajero = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));

        var cola = await Leer<ColaControlPrintDto>(await http.GetAsync($"api/sucursales/{caja.SucursalId}/impresiones/control-print?usuarioId={cajero.UsuarioId}"));
        var resumen = await Leer<ResumenPorCobrarDto>(await http.GetAsync($"api/sucursales/{caja.SucursalId}/impresiones/por-cobrar/cuantas"));
        var estado = await Leer<EstadoControlPrintDto>(await http.GetAsync("api/controlprint"));

        Assert.Equal((false, 0), (cola.Configurado, cola.Trabajos.Count));
        Assert.Equal((false, (bool?)null), (resumen.ControlPrintConfigurado, resumen.ControlPrintContesta));
        Assert.Equal((false, (string?)null), (estado.Configurado, estado.Error));
    }

    // ── Lo que imprime la caja 2 ─────────────────────────────────────────────────────────────────────────────────

    private static TrabajoMedidoDto Medido(Caja c, string documento) =>
        new(Guid.NewGuid(), c.Datos.Id, null, "CAJA2", "WF-6590 Series(Red)", 7, documento, "cajera", DateTime.UtcNow, EstadoTrabajo.Impreso,
            new AjustesTrabajoDto("Standard", "Plain", "NorthAmericaLetter", 215.9, 279.4, false, false, 1, 1, true),
            [new CoberturaPaginaDto(0, 0, 0, 0.05, 0.05, 93.5, false)], true, null, null, DateTime.UtcNow.AddSeconds(-20));

    [Fact]
    public async Task Lo_que_imprime_la_caja_2_se_cobra_con_los_costos_de_Control_Print_y_sale_junto_a_sus_tarjetas()
    {
        // En Control Print: el mínimo es de $9.
        _controlPrint.CambiarCostos(JsonSerializer.Serialize(new CostSettings { MinimumPrice = 9, RoundTo = 1 }, CostSettings.Json));
        Agregar(Trabajo(1234, Hace(4), precio: 15));
        var c = await AbrirAsync();
        await HastaQueAsync(c, x => x.Trabajos.Count == 1, "la tarjeta de Control Print");

        var medido = Medido(c, "Recibo de luz.pdf");
        await Leer<object>(await c.Http.PostAsJsonAsync("api/impresiones", medido));

        var deLaCaja = (await c.Http.GetFromJsonAsync<List<ImpresionPorCobrarDto>>(
            $"api/sucursales/{c.Datos.SucursalId}/impresiones/por-cobrar?usuarioId={c.Cajero.UsuarioId}"))!.Single(t => t.Id == medido.Id);
        Assert.Equal(("Recibo de luz.pdf", "CAJA2", 900L), (deLaCaja.Documento, deLaCaja.Pc, deLaCaja.PrecioCentavos));
        // Los costos de Control Print quedaron como los de la sucursal (para cuando no conteste al arrancar).
        await using (var db = _servicio.Base())
        {
            var guardados = (await db.Set<ConfiguracionImpresion>().SingleAsync(x => x.SucursalId == c.Datos.SucursalId)).CostosJson;
            Assert.Equal(9m, JsonSerializer.Deserialize<CostSettings>(guardados, CostSettings.Json)!.MinimumPrice);
        }

        // Las dos fuentes en el mismo ticket, en una sola línea: la de Control Print y la de la caja 2.
        var ticket = await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas",
            Cobro(c, [new ImpresionDeControlPrintSolicitada(1234, 1500)], [medido.Id])));
        var linea = Assert.Single(ticket.Lineas);
        Assert.Equal(("Impresiones #1234 y 1 de CAJA2", 2400L), (linea.Descripcion, linea.ImporteCentavos));
        Assert.Empty((await ColaAsync(c)).Trabajos);
        Assert.Equal(0, (await ResumenAsync(c)).DeLaSucursal);
        Assert.Empty(_controlPrint.Escrituras);
    }
}
