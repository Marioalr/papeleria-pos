using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
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
/// El piloto, pasos 3 y 4 (docs/migracion-eleventa.md, «Mandar cada venta» y lo que sigue): cada venta cobrada entra a la
/// bandeja hacia eleventa; al cobrar se intenta ya, y el ticket sale con el folio de eleventa si contestó a tiempo. Detrás
/// van sus cancelaciones y devoluciones y las entradas y salidas de efectivo. El cierre del turno no: mientras dure el
/// piloto, el corte se hace en eleventa (CorteEnElPilotoTests).
/// </summary>
public class EnvioEleventaApiTests
{
    private const int CajaEleventa = 4;

    private static (string, string)[] Ajustes(string servidor, int puerto, string ruta, double segundosAlCobrar = 10) =>
    [
        ("Eleventa:Escribir", "true"), ("Eleventa:Servidor", servidor), ("Eleventa:Puerto", puerto.ToString(CultureInfo.InvariantCulture)),
        ("Eleventa:Base", ruta), ("Eleventa:Usuario", "SYSDBA"),
        ("Eleventa:Clave", Environment.GetEnvironmentVariable("PRUEBAS_FIREBIRD_CLAVE") is { Length: > 0 } clave ? clave : "masterkey"),
        ("Eleventa:Caja", CajaEleventa.ToString(CultureInfo.InvariantCulture)), ("Eleventa:Cajero", "7"),
        ("Eleventa:SegundosAlCobrar", segundosAlCobrar.ToString(CultureInfo.InvariantCulture)),
        // Que en la prueba solo mande el cobro o quien lo pida, no la vuelta de fondo.
        ("Eleventa:SegundosEntreEnvios", "3600"),
    ];

    private static (string, string)[] Ajustes(ServidorEleventa e) => Ajustes(e.Servidor, e.Puerto, e.Base);

    private static async Task<T> Leer<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<T>())!;
    }

    private sealed record Caja(HttpClient Http, CajaDto Datos, SesionDto Sesion, TurnoDto Turno, ResultadoBusquedaDto Producto);

    private static async Task<Caja> AbrirAsync(ServicioDePrueba servicio)
    {
        var http = servicio.Fabrica.CreateClient();
        var caja = (await http.GetFromJsonAsync<List<CajaDto>>("api/cajas"))!.Single();
        var sesion = await Leer<SesionDto>(await http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("cajero", "1111")));
        var turno = await Leer<TurnoDto>(await http.PostAsJsonAsync("api/turnos", new SolicitudTurno(caja.Id, sesion.UsuarioId, 50000)));
        var producto = (await http.GetFromJsonAsync<List<ResultadoBusquedaDto>>($"api/productos/buscar?q=pluma%20punto%20fino&sucursalId={caja.SucursalId}"))!
            .First(r => r.Codigo is not null);
        return new Caja(http, caja, sesion, turno, producto);
    }

    /// <summary>Dos del producto y un artículo común de $15, en efectivo.</summary>
    private static async Task<TicketDto> CobrarAsync(Caja c)
    {
        var total = 2 * c.Producto.PrecioCentavos + 1500;
        return await Leer<TicketDto>(await c.Http.PostAsJsonAsync("api/ventas", new SolicitudCobro(c.Datos.Id, c.Turno.Id, c.Sesion.UsuarioId,
            [new LineaSolicitada(c.Producto.VarianteId, null, 2000), new LineaSolicitada(null, null, 1000, "Recuerdo", 1500)],
            [new CobroSolicitado(FormaPago.Efectivo, total + 10000)])));
    }

    private static async Task<EstadoEnviosEleventaDto> EnviosAsync(Caja c) => (await c.Http.GetFromJsonAsync<EstadoEnviosEleventaDto>("api/eleventa/envios"))!;

    /// <param name="abierta">
    /// Si la caja ya está abierta en eleventa: en el piloto el turno lo abre eleventa con su fondo, no el puente
    /// (CorteEnElPiloto).
    /// </param>
    private static async Task<EleventaDePrueba> EleventaAsync(bool conComunes = true, bool abierta = true)
    {
        var e = await EleventaDePrueba.CrearAsync();
        await e.EjecutarAsync("INSERT INTO CAJAS (ID, NOMBRE, ULTIMA_IP) VALUES (4, 'Caja 2', '10.0.0.19')");
        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (5, 'Papelería')");
        if (conComunes)
            await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (9, @nombre)", ("nombre", EnvioEleventa.DepartamentoComunes));
        await e.ProductoAsync(10, "PLU-FINA", "Pluma punto fino", depto: 5, costo: 4, precio: 9, existencia: 50);
        await e.EjecutarAsync("INSERT INTO VENTATICKETS (FOLIO, CAJA_ID, CAJERO_ID, OPERACION_ID, ESTA_ABIERTO, VENDIDO_EN) VALUES (41, 1, 1, -1, 'f', CURRENT_TIMESTAMP)");
        if (abierta)
            await e.AbrirCajaAsync(CajaEleventa, cajero: 7, AperturaEnEleventa(), fondo: 300);
        return e;
    }

    /// <summary>Cuando la cajera abrió la caja en eleventa: un rato antes, en la hora de la tienda.</summary>
    private static DateTime AperturaEnEleventa()
    {
        var hora = HoraMexico.ALocal(DateTime.UtcNow).AddMinutes(-30);
        return new DateTime(hora.Ticks - hora.Ticks % TimeSpan.TicksPerSecond);
    }

    /// <summary>La pluma de la caja vino del producto 10 de eleventa (como lo deja la importación).</summary>
    private static async Task MapearAsync(ServicioDePrueba servicio, Guid varianteId)
    {
        await using var db = servicio.Base();
        db.MapeosImportacion.Add(new MapeoImportacion { Origen = SincronizacionEleventa.Origen, Entidad = nameof(Variante), IdOrigen = "10", IdNuevo = varianteId });
        await db.SaveChangesAsync();
    }

    [FactConFirebird]
    public async Task Al_cobrar_el_ticket_sale_con_el_folio_de_eleventa_y_la_devolucion_lo_encuentra()
    {
        await using var e = await EleventaAsync();
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio);
        await MapearAsync(servicio, c.Producto.VarianteId);

        var ticket = await CobrarAsync(c);

        Assert.Equal((42, "42"), (ticket.FolioEleventa, ticket.FolioImpreso));
        Assert.StartsWith("A-", ticket.Folio);
        var enEleventa = (await e.FilasAsync("SELECT * FROM VENTATICKETS WHERE FOLIO = 42")).Single();
        Assert.Equal(CajaEleventa, Convert.ToInt32(enEleventa["CAJA_ID"]));
        Assert.Equal(ticket.TotalCobradoCentavos / 100.0, Convert.ToDouble(enEleventa["TOTAL"]), 2);
        // Las líneas de la venta no guardan su orden: se buscan por su código.
        var articulos = await e.FilasAsync("SELECT PRODUCTO_CODIGO, CANTIDAD FROM VENTATICKETS_ARTICULOS WHERE TICKET_ID = @t", ("t", enEleventa["ID"]));
        Assert.Equal(2, articulos.Count);
        var pluma = Assert.Single(articulos, a => (string)a["PRODUCTO_CODIGO"]! == "PLU-FINA");
        Assert.Equal(2, Convert.ToDouble(pluma["CANTIDAD"]), 3);
        Assert.Single(articulos, a => ((string)a["PRODUCTO_CODIGO"]!).StartsWith("COMUN-", StringComparison.Ordinal));
        Assert.Equal(48, Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 10")), 3);

        // El folio de eleventa abre la devolución en la caja nueva, y la reimpresión también lo lleva.
        var porFolio = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/42"));
        Assert.Equal(ticket.Folio, porFolio.Folio);
        Assert.Equal(42, (await Leer<TicketDto>(await c.Http.GetAsync($"api/ventas/{ticket.VentaId}/ticket"))).FolioEleventa);
        var envios = await EnviosAsync(c);
        Assert.Equal((true, 0, 0), (envios.Activo, envios.Pendientes, envios.PorRevisar));
    }

    [FactConFirebird]
    public async Task Si_no_entra_queda_en_la_bandeja_y_se_manda_despues_sin_duplicarse()
    {
        // Sin el departamento de los comunes, eleventa no puede recibir el artículo común: no es la red.
        await using var e = await EleventaAsync(conComunes: false);
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio);

        var ticket = await CobrarAsync(c);

        Assert.Null(ticket.FolioEleventa);
        Assert.Equal(ticket.Folio, ticket.FolioImpreso);
        var antes = await EnviosAsync(c);
        Assert.Equal(1, antes.Pendientes);
        Assert.Contains(EnvioEleventa.DepartamentoComunes, antes.PorAtender.Single().Error);
        // Eleventa la rechazó: allá no está, y la lectura la descuenta como lo pendiente.
        var enCurso = servicio.Fabrica.Services.GetRequiredService<EnvioVentasEleventa>().EnCurso;
        using (var vigilancia = enCurso.Vigilar())
            Assert.Equal([antes.PorAtender.Single().Id], vigilancia.Rechazados());

        await e.EjecutarAsync("INSERT INTO DEPARTAMENTOS (ID, NOMBRE) VALUES (9, @nombre)", ("nombre", EnvioEleventa.DepartamentoComunes));
        Assert.Equal(HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
        var despues = await EsperarAsync(c, x => x.Pendientes == 0);

        Assert.Empty(despues.PorAtender);
        using (var vigilancia = enCurso.Vigilar())
            Assert.Empty(vigilancia.Rechazados());
        Assert.Equal(42, (await Leer<TicketDto>(await c.Http.GetAsync($"api/ventas/{ticket.VentaId}/ticket"))).FolioEleventa);
        Assert.Equal(1, Convert.ToInt32(await e.ValorAsync("SELECT COUNT(*) FROM VENTATICKETS WHERE CAJA_ID = 4 AND ESTA_ABIERTO = 'f'")));
    }

    [FactConFirebird]
    public async Task Sin_la_caja_abierta_en_eleventa_la_venta_espera_y_entra_al_turno_que_abre_eleventa()
    {
        // Después del corte en eleventa y antes de volver a abrir la caja allá: el puente no abre el turno (CorteEnElPiloto).
        await using var e = await EleventaAsync(abierta: false);
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio);
        await MapearAsync(servicio, c.Producto.VarianteId);

        var ticket = await CobrarAsync(c);

        Assert.Null(ticket.FolioEleventa);
        var antes = await EnviosAsync(c);
        Assert.Equal((1, CorteEnElPiloto.AbreLaCajaEnEleventa), (antes.Pendientes, antes.PorAtender.Single().Error));
        Assert.Empty(await e.FilasAsync("SELECT ID FROM TURNOS"));

        var turno = await e.AbrirCajaAsync(CajaEleventa, cajero: 7, AperturaEnEleventa(), fondo: 300);
        Assert.Equal(HttpStatusCode.Accepted, (await c.Http.PostAsync("api/eleventa/enviar", null)).StatusCode);
        var despues = await EsperarAsync(c, x => x.Pendientes == 0);

        Assert.Empty(despues.PorAtender);
        var enEleventa = (await e.FilasAsync("SELECT T.ID, T.DINERO_INICIAL FROM TURNOS T")).Single();
        Assert.Equal((turno, 300m), (Convert.ToInt32(enEleventa["ID"]), (decimal)enEleventa["DINERO_INICIAL"]!));
        Assert.Equal(turno, Convert.ToInt32(await e.ValorAsync("SELECT TURNO_ID FROM VENTATICKETS WHERE CAJA_ID = 4 AND ESTA_ABIERTO = 'f'")));
    }

    [Fact]
    public async Task Si_eleventa_no_contesta_el_ticket_sale_con_el_folio_de_la_caja_y_el_siguiente_cobro_ya_no_espera()
    {
        // Nadie escucha en ese puerto: como la caja principal apagada.
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes("127.0.0.1", 1, @"C:\eleventa\PDVDATA.FDB", segundosAlCobrar: 20));
        var c = await AbrirAsync(servicio);

        var primero = await CobrarAsync(c);
        var reloj = Stopwatch.StartNew();
        var segundo = await CobrarAsync(c);
        reloj.Stop();

        Assert.Equal((primero.Folio, segundo.Folio), (primero.FolioImpreso, segundo.FolioImpreso));
        Assert.Null(primero.FolioEleventa);
        // Después de la falla de red, el cobro no espera a eleventa: la venta se manda en la vuelta.
        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(5), $"El segundo cobro tardó {reloj.Elapsed}.");
        var envios = await EnviosAsync(c);
        Assert.Equal(2, envios.Pendientes);
        Assert.StartsWith("No contesta el eleventa de 127.0.0.1", envios.UltimoError);
        Assert.True(envios.PorAtender[0].Intentos >= 1);
    }

    [Fact]
    public async Task Lo_cobrado_con_tarjeta_queda_por_revisar()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes("127.0.0.1", 1, @"C:\eleventa\PDVDATA.FDB"));
        var c = await AbrirAsync(servicio);

        // En el piloto la caja solo cobra en efectivo (SoloEfectivoEnElPilotoTests); con tarjeta solo llega lo que se cobró
        // sin conexión antes de saberlo, y eso ya pasó: se guarda y va a la bandeja.
        var paquete = (await c.Http.GetFromJsonAsync<PaqueteContingenciaDto>($"api/cajas/{c.Datos.Id}/paquete"))!;
        var venta = new VentaContingenciaDto(Guid.NewGuid(), c.Datos.Id, c.Turno.Id, c.Sesion.UsuarioId, paquete.UltimoFolio + 1, DateTime.UtcNow,
            [new LineaContingenciaDto(null, null, "Recuerdo", 1000, 1500)], [new CobroSolicitado(FormaPago.Tarjeta, 1500, "4821")]);
        Assert.True((await c.Http.PostAsJsonAsync("api/bandeja/ventas", venta)).IsSuccessStatusCode);
        // La vuelta de fondo la revisa (el estado se lee en dos consultas: se espera a que las dos lo digan).
        var envios = await EsperarAsync(c, x => x.PorRevisar == 1 && x.PorAtender.All(e => e.Estado == EstadoEnvioEleventa.PorRevisar));

        Assert.Equal((0, 1), (envios.Pendientes, envios.PorRevisar));
        Assert.Equal((EstadoEnvioEleventa.PorRevisar, 0), (envios.PorAtender.Single().Estado, envios.PorAtender.Single().Intentos));
        Assert.Contains("tarjeta", envios.PorAtender.Single().Error);
    }

    [Fact]
    public async Task Sin_escribir_a_eleventa_no_hay_bandeja()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync();
        var c = await AbrirAsync(servicio);

        var ticket = await CobrarAsync(c);
        await MovimientoAsync(c, TipoMovimientoCaja.Salida, 10000, "Garrafón de agua");

        Assert.Null(ticket.FolioEleventa);
        var envios = await EnviosAsync(c);
        Assert.Equal((false, 0, null), (envios.Activo, envios.Pendientes, envios.Problema));
        await using var db = servicio.Base();
        Assert.Empty(db.EnviosAEleventa);
    }

    private static async Task<ComprobanteMovimientoDto> MovimientoAsync(Caja c, TipoMovimientoCaja tipo, long montoCentavos, string motivo) =>
        await Leer<ComprobanteMovimientoDto>(await c.Http.PostAsJsonAsync($"api/turnos/{c.Turno.Id}/movimientos",
            new SolicitudMovimiento(tipo, montoCentavos, motivo, c.Sesion.UsuarioId)));

    [Fact]
    public async Task Las_entradas_y_salidas_entran_a_la_bandeja_con_el_movimiento_tambien_las_de_contingencia()
    {
        // Nadie escucha en ese puerto: como la caja principal apagada.
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes("127.0.0.1", 1, @"C:\eleventa\PDVDATA.FDB", segundosAlCobrar: 20));
        var c = await AbrirAsync(servicio);

        // Antes de la primera venta: eleventa también abre el turno para una entrada.
        await MovimientoAsync(c, TipoMovimientoCaja.Entrada, 20000, "Cambio del banco");
        var sinConexion = await c.Http.PostAsJsonAsync($"api/bandeja/turnos/{c.Turno.Id}/movimientos",
            new MovimientoContingenciaDto(Guid.NewGuid(), TipoMovimientoCaja.Salida, 10000, "Garrafón de agua", c.Sesion.UsuarioId, DateTime.UtcNow));
        // Se intentó ya, sin esperar la vuelta de fondo.
        var envios = await EsperarAsync(c, x => x.PorAtender.Count > 0 && x.PorAtender[0].Intentos >= 1);

        Assert.True(sinConexion.IsSuccessStatusCode, await sinConexion.Content.ReadAsStringAsync());
        Assert.Equal(2, envios.Pendientes);
        Assert.Equal([TipoEnvioEleventa.MovimientoDeCaja, TipoEnvioEleventa.MovimientoDeCaja], envios.PorAtender.Select(x => x.Tipo));
        Assert.Equal(["Entrada de efectivo: Cambio del banco", "Salida de efectivo: Garrafón de agua"], envios.PorAtender.Select(x => x.Descripcion));
        Assert.Equal([20000L, 10000], envios.PorAtender.Select(x => x.TotalCentavos));
        Assert.StartsWith("No contesta el eleventa de 127.0.0.1", envios.UltimoError);
    }

    [FactConFirebird]
    public async Task Las_entradas_y_salidas_llegan_a_eleventa_y_su_turno_sigue_abierto_para_su_corte()
    {
        await using var e = await EleventaAsync();
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, USUARIO) VALUES (7, 'cajera2')");
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio);
        await MapearAsync(servicio, c.Producto.VarianteId);

        // La entrada, antes de la primera venta, va al turno que abrió eleventa y se manda ya, sin esperar la vuelta.
        await MovimientoAsync(c, TipoMovimientoCaja.Entrada, 20000, "Cambio del banco");
        var trasLaEntrada = await EsperarAsync(c, x => x.Pendientes == 0);
        var ticket = await CobrarAsync(c);
        await MovimientoAsync(c, TipoMovimientoCaja.Salida, 10000, "Garrafón de agua");
        var envios = await EsperarAsync(c, x => x.Pendientes == 0);
        // Mientras dure el piloto, el corte se hace en eleventa: el de la caja nueva no se hace ni cierra el turno de allá.
        var corte = await c.Http.PostAsJsonAsync($"api/turnos/{c.Turno.Id}/corte-z",
            new SolicitudCorteZ("encargado", "2222", new Dictionary<long, int> { [50000] = 1, [20000] = 1 }));

        Assert.Equal(0, trasLaEntrada.Pendientes);
        Assert.Equal((0, 0), (envios.Pendientes, envios.PorRevisar));
        Assert.Equal(HttpStatusCode.BadRequest, corte.StatusCode);
        // Un solo turno en eleventa: el que abrió eleventa, con la entrada, la venta y la salida, y sigue abierto para su corte.
        var turno = (await e.FilasAsync("SELECT * FROM TURNOS")).Single();
        Assert.Null(turno["TERMINO_EN"]);
        var venta = (await e.FilasAsync("SELECT TOTAL, TURNO_ID FROM VENTATICKETS WHERE CAJA_ID = 4 AND ESTA_ABIERTO = 'f'")).Single();
        Assert.Equal((ticket.TotalCobradoCentavos / 100.0, turno["ID"]), (Math.Round(Convert.ToDouble(venta["TOTAL"]), 2), venta["TURNO_ID"]));
        var manuales = await e.FilasAsync("SELECT TIPO, MONTO, DESCRIPCION, ID_TURNO FROM CORTE_MOVIMIENTOS WHERE DESCRIPCION NOT STARTING WITH 'Efectivo ' ORDER BY ID");
        Assert.Equal([("Entrada", 200m, "Cambio del banco"), ("Salida", 100m, "Garrafón de agua")],
            manuales.Select(m => ((string)m["TIPO"]!, (decimal)m["MONTO"]!, (string)m["DESCRIPCION"]!)));
        Assert.All(manuales, m => Assert.Equal(turno["ID"], m["ID_TURNO"]));
        Assert.Equal((200m, 100m), ((decimal)turno["ACUMULADO_ENTRADAS"]!, (decimal)turno["ACUMULADO_SALIDAS"]!));
        Assert.Empty(await e.FilasAsync("SELECT TIPO FROM CORTE_MOVIMIENTOS WHERE DESCRIPCION STARTING WITH 'Efectivo '"));
    }

    private static async Task<SesionDto> EncargadoAsync(Caja c) =>
        await Leer<SesionDto>(await c.Http.PostAsJsonAsync("api/sesion", new SolicitudEntrada("encargado", "2222")));

    [FactConFirebird]
    public async Task La_cancelacion_y_la_devolucion_llegan_a_eleventa_en_orden_y_su_turno_sigue_abierto()
    {
        await using var e = await EleventaAsync();
        await e.EjecutarAsync("INSERT INTO USUARIOS (ID, USUARIO) VALUES (7, 'cajera2')");
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes(e.Servidor));
        var c = await AbrirAsync(servicio);
        await MapearAsync(servicio, c.Producto.VarianteId);
        var encargado = await EncargadoAsync(c);

        var primero = await CobrarAsync(c);
        var segundo = await CobrarAsync(c);
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{primero.VentaId}/cancelacion",
            new SolicitudCancelacion(encargado.UsuarioId, c.Datos.Id, "Se arrepintió")));
        var paraDevolver = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{segundo.FolioEleventa}"));
        var pluma = paraDevolver.Lineas.Single(l => l.LlevaInventario);
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{segundo.VentaId}/devoluciones",
            new SolicitudDevolucion(encargado.UsuarioId, c.Datos.Id, "No era la que buscaba", [new LineaDevolucionSolicitada(pluma.LineaId, 1000)])));
        var envios = await EsperarAsync(c, x => x.Pendientes == 0);

        Assert.Equal((0, 0), (envios.Pendientes, envios.PorRevisar));
        Assert.Equal((42, 43), (primero.FolioEleventa, segundo.FolioEleventa));
        Assert.Equal("t", (string)(await e.ValorAsync("SELECT ESTA_CANCELADO FROM VENTATICKETS WHERE FOLIO = 42"))!);
        Assert.Equal(["C", "P"], (await e.FilasAsync("SELECT TIPO_DEVOLUCION FROM DEVOLUCIONES ORDER BY ID")).Select(d => (string)d["TIPO_DEVOLUCION"]!));
        // 50 − 2 − 2 + 2 de la cancelación + 1 de la devolución.
        Assert.Equal(49, Convert.ToDouble(await e.ValorAsync("SELECT CANTIDAD_ACTUAL FROM INVENTARIO_BALANCES WHERE PRODUCTO_ID = 10")), 3);
        // Mientras dure el piloto, el corte se hace en eleventa: el turno de allá sigue abierto.
        Assert.Null((await e.FilasAsync("SELECT * FROM TURNOS")).Single()["TERMINO_EN"]);
    }

    [Fact]
    public async Task Lo_cancelado_antes_de_llegar_a_eleventa_ya_no_se_manda()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes("127.0.0.1", 1, @"C:\eleventa\PDVDATA.FDB", segundosAlCobrar: 20));
        var c = await AbrirAsync(servicio);
        var encargado = await EncargadoAsync(c);
        var ticket = await CobrarAsync(c);
        Assert.Equal(1, (await EnviosAsync(c)).Pendientes);

        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/cancelacion",
            new SolicitudCancelacion(encargado.UsuarioId, c.Datos.Id, "Se arrepintió")));
        var envios = await EsperarAsync(c, x => x.Pendientes == 0);

        // La venta y su cancelación se descartan sin buscar a eleventa: allá nunca existió.
        Assert.Equal((0, 0), (envios.Pendientes, envios.PorRevisar));
        await using var db = servicio.Base();
        Assert.All(db.EnviosAEleventa.ToList(), x => Assert.Equal(EstadoEnvioEleventa.Descartada, x.Estado));
        Assert.Equal([TipoEnvioEleventa.Venta, TipoEnvioEleventa.Cancelacion], db.EnviosAEleventa.Select(x => x.Tipo).AsEnumerable().Order());
    }

    [Fact]
    public async Task Lo_que_no_entra_se_puede_dejar_por_revisar_para_que_lo_demas_pase()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes("127.0.0.1", 1, @"C:\eleventa\PDVDATA.FDB", segundosAlCobrar: 20));
        var c = await AbrirAsync(servicio);
        await CobrarAsync(c);
        var pendiente = (await EnviosAsync(c)).PorAtender.Single();

        var dejado = await Leer<EstadoEnviosEleventaDto>(await c.Http.PostAsync($"api/eleventa/envios/{pendiente.Id}/revisar", null));
        var otraVez = await c.Http.PostAsync($"api/eleventa/envios/{pendiente.Id}/revisar", null);

        Assert.Equal((0, 1), (dejado.Pendientes, dejado.PorRevisar));
        Assert.StartsWith("Se dejó para revisar a mano.", dejado.PorAtender.Single().Error);
        Assert.Equal(HttpStatusCode.BadRequest, otraVez.StatusCode);
    }

    [Fact]
    public async Task Lo_que_ya_quedo_en_eleventa_a_mano_ya_no_se_manda_y_su_devolucion_tambien_va_a_mano()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes("127.0.0.1", 1, @"C:\eleventa\PDVDATA.FDB", segundosAlCobrar: 20));
        var c = await AbrirAsync(servicio);
        var encargado = await EncargadoAsync(c);
        var ticket = await CobrarAsync(c);
        var pendiente = (await EnviosAsync(c)).PorAtender.Single();

        var aMano = await Leer<EstadoEnviosEleventaDto>(await c.Http.PostAsync($"api/eleventa/envios/{pendiente.Id}/a-mano", null));
        var otraVez = await c.Http.PostAsync($"api/eleventa/envios/{pendiente.Id}/a-mano", null);
        var paraDevolver = await Leer<VentaParaDevolverDto>(await c.Http.GetAsync($"api/sucursales/{c.Datos.SucursalId}/ventas/folio/{ticket.Folio}"));
        await Leer<ComprobanteDevolucionDto>(await c.Http.PostAsJsonAsync($"api/ventas/{ticket.VentaId}/devoluciones",
            new SolicitudDevolucion(encargado.UsuarioId, c.Datos.Id, "No era la que buscaba",
                [new LineaDevolucionSolicitada(paraDevolver.Lineas.Single(l => l.LlevaInventario).LineaId, 1000)])));

        Assert.Equal((0, 0), (aMano.Pendientes, aMano.PorRevisar));
        Assert.Empty(aMano.PorAtender);
        Assert.Equal(HttpStatusCode.BadRequest, otraVez.StatusCode);
        await using var db = servicio.Base();
        Assert.Equal(EstadoEnvioEleventa.AMano, db.EnviosAEleventa.Single(x => x.Id == pendiente.Id).Estado);
        // Su ticket en eleventa no se conoce aquí: la devolución no va sola, también se anota a mano.
        var devolucion = await VentasParaEleventa.ArmarDevolucionAsync(db, db.EnviosAEleventa.Single(x => x.Tipo == TipoEnvioEleventa.Devolucion).DevolucionId!.Value);
        Assert.Equal((null, false), (devolucion.Devolucion, devolucion.Descartada));
        Assert.Contains("a mano", devolucion.PorRevisar);
    }

    [Fact]
    public async Task La_lectura_de_eleventa_ve_lo_que_la_bandeja_manda_mientras_lee()
    {
        // Nadie escucha en ese puerto: el intento al cobrar falla enseguida, pero sí se intentó.
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes("127.0.0.1", 1, @"C:\eleventa\PDVDATA.FDB", segundosAlCobrar: 20));
        var c = await AbrirAsync(servicio);
        var envios = servicio.Fabrica.Services.GetRequiredService<EnvioVentasEleventa>();

        using var vigilancia = envios.EnCurso.Vigilar();
        await CobrarAsync(c);

        Assert.Equal([(await EnviosAsync(c)).PorAtender.Single().Id], vigilancia.Mandados());
        // Fue la red: no se sabe si entró, no es que eleventa la rechazara.
        Assert.Empty(vigilancia.Rechazados());
    }

    [Fact]
    public async Task Lo_que_quedo_por_revisar_se_anota_a_mano_y_la_lectura_en_curso_lo_ve()
    {
        await using var servicio = await ServicioDePrueba.CrearAsync(Ajustes("127.0.0.1", 1, @"C:\eleventa\PDVDATA.FDB", segundosAlCobrar: 20));
        var c = await AbrirAsync(servicio);
        await CobrarAsync(c);
        var pendiente = (await EnviosAsync(c)).PorAtender.Single();
        // Se dejó por revisar; después alguien la registra en eleventa a mano mientras se lee eleventa.
        await Leer<EstadoEnviosEleventaDto>(await c.Http.PostAsync($"api/eleventa/envios/{pendiente.Id}/revisar", null));
        var envios = servicio.Fabrica.Services.GetRequiredService<EnvioVentasEleventa>();

        using var vigilancia = envios.EnCurso.Vigilar();
        var aMano = await Leer<EstadoEnviosEleventaDto>(await c.Http.PostAsync($"api/eleventa/envios/{pendiente.Id}/a-mano", null));

        Assert.Equal((0, 0), (aMano.Pendientes, aMano.PorRevisar));
        Assert.Equal([pendiente.Id], vigilancia.Mandados());
        await using var db = servicio.Base();
        var envio = db.EnviosAEleventa.Single(x => x.Id == pendiente.Id);
        Assert.Equal(EstadoEnvioEleventa.AMano, envio.Estado);
        Assert.StartsWith("Se anotó a mano en eleventa. Se dejó para revisar a mano.", envio.Error);
    }

    private static async Task<EstadoEnviosEleventaDto> EsperarAsync(Caja c, Func<EstadoEnviosEleventaDto, bool> listo)
    {
        for (var intento = 0; ; intento++)
        {
            var estado = await EnviosAsync(c);
            if (listo(estado) || intento == 100)
                return estado;
            await Task.Delay(100);
        }
    }
}
